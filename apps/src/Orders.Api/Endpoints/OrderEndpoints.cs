using Orders.Api.Authorization;
using Orders.Api.Contracts;
using Orders.Api.Middleware;
using Orders.Api.RateLimiting;
using Orders.Application.Exceptions;
using Orders.Application.Ports;
using Orders.Application.UseCases.CreateOrder;
using Orders.Application.UseCases.GetOrder;
using Orders.Domain;

namespace Orders.Api.Endpoints;

public static class OrderEndpoints
{
    public static IEndpointRouteBuilder MapOrderEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/orders").WithTags("Orders").RequireRateLimiting(RateLimitingExtensions.OrdersPolicy);

        group.MapPost("", CreateAsync).RequireAuthorization(OrdersAuthorizationPolicies.Write);
        group.MapGet("/{id:guid}", GetByIdAsync).RequireAuthorization(OrdersAuthorizationPolicies.Read);

        return endpoints;
    }

    private static async Task<IResult> CreateAsync(
        CreateOrderRequest request,
        CreateOrderHandler handler,
        IConfiguration configuration,
        HttpContext httpContext,
        CancellationToken cancellationToken)
    {
        var correlationId = httpContext.GetCorrelationId();
        var instanceId = configuration["InstanceId"] ?? Environment.MachineName;
        var idempotencyKey = httpContext.Request.Headers["Idempotency-Key"].FirstOrDefault();

        // A plain shopper token places an order for exactly
        // one customer - itself. The body's customerId is honored only for
        // an admin caller (a support agent creating an order on someone
        // else's behalf); for everyone else it is overwritten, not merely
        // validated against, so there is no window where a crafted body
        // value is ever used even transiently.
        var customerId = httpContext.IsAdmin() ? request.CustomerId : httpContext.GetCustomerId();
        var command = new CreateOrderCommand(
            customerId,
            request.Amount,
            request.Currency,
            correlationId,
            instanceId,
            idempotencyKey,
            request.Items?.Select(item => new CreateOrderItem(item.Sku, item.Quantity)).ToList(),
            request.CouponCode,
            request.PaymentMethod,
            request.ShippingAddress is { } address
                ? new ShippingAddress(address.Line1 ?? "", address.City ?? "", address.Region ?? "", address.PostalCode ?? "")
                : null,
            request.ExpectedSubtotal);

        CreateOrderResult result;
        try
        {
            result = await handler.HandleAsync(command, cancellationToken);
        }
        catch (InfrastructureUnavailableException)
        {
            return ServiceUnavailable(httpContext, "PostgreSQL is currently unavailable.");
        }
        catch (CouponRedemptionUnavailableException exception)
        {
            // The coupon passed the advisory eligibility check
            // and then lost the race for the last slot. 409, not 400 -
            // nothing about the request was wrong, it simply arrived second,
            // and resubmitting it unchanged would be equally valid and
            // equally unlucky.
            return Results.Problem(
                detail: exception.Message,
                statusCode: StatusCodes.Status409Conflict,
                title: "Coupon Unavailable");
        }

        // Distinct from a validation error - nothing about
        // the request is malformed, the catalog moved under it. 409, the
        // same "well-formed but the world changed" status M67 already uses
        // for a coupon losing its last slot mid-request, not a 400 that
        // would tell the caller to fix a request that was never wrong.
        if (result.PriceMismatch is { } mismatch)
        {
            return Results.Problem(
                detail: $"The price has changed since it was last seen. Expected subtotal {mismatch.ExpectedSubtotal:0.00}, now {mismatch.ActualSubtotal:0.00}.",
                statusCode: StatusCodes.Status409Conflict,
                title: "Price Changed",
                extensions: new Dictionary<string, object?>
                {
                    ["expectedSubtotal"] = mismatch.ExpectedSubtotal,
                    ["actualSubtotal"] = mismatch.ActualSubtotal
                });
        }

        if (!result.IsValid)
        {
            return Results.ValidationProblem(result.ValidationErrors);
        }

        var response = ToResponse(result.Order!, correlationId, instanceId);
        if (result.WasReplayed)
        {
            httpContext.Response.Headers["Idempotency-Replayed"] = "true";
            return Results.Ok(response);
        }

        return Results.Created($"/orders/{result.Order!.Id}", response);
    }

    private static async Task<IResult> GetByIdAsync(
        Guid id,
        GetOrderHandler handler,
        IConfiguration configuration,
        HttpContext httpContext,
        CancellationToken cancellationToken)
    {
        GetOrderResult result;
        try
        {
            result = await handler.HandleAsync(id, cancellationToken);
        }
        catch (InfrastructureUnavailableException)
        {
            return ServiceUnavailable(httpContext, "PostgreSQL is currently unavailable.");
        }

        // 404, not 403 - a non-owner gets the same answer as
        // a genuinely missing id, so probing ids can't be used to learn
        // which ones exist. This is deliberately checked after the cache
        // lookup, not before: the order still has to be fetched to know
        // whose it is, the same as any other field on it.
        if (result.Order is null || !httpContext.MayAccess(result.Order.CustomerId))
        {
            return Results.NotFound();
        }

        httpContext.Response.Headers["X-Cache"] = result.LookupResult switch
        {
            CacheLookupResult.Hit => "HIT",
            CacheLookupResult.Bypassed => "BYPASS",
            _ => "MISS"
        };

        return Results.Ok(ToResponse(
            result.Order,
            httpContext.GetCorrelationId(),
            configuration["InstanceId"] ?? Environment.MachineName));
    }

    private static IResult ServiceUnavailable(HttpContext httpContext, string detail)
    {
        httpContext.Response.Headers["Retry-After"] = "5";
        return Results.Problem(
            detail: detail,
            statusCode: StatusCodes.Status503ServiceUnavailable,
            title: "Service Unavailable");
    }

    private static OrderResponse ToResponse(
        Order order,
        string correlationId,
        string instanceId)
    {
        return new OrderResponse(
            order.Id,
            order.CustomerId,
            order.Amount,
            order.Currency,
            order.Status,
            order.CreatedAt,
            correlationId,
            instanceId,
            ToPricingResponse(order),
            order.PaymentMethod);
    }

    // An amount-only order reports no breakdown at all rather than one full
    // of zeroes - "this order has no pricing detail" and "this order had
    // nothing discounted" are different statements.
    private static OrderPricingResponse? ToPricingResponse(Order order)
    {
        if (order.Lines.Count == 0)
        {
            return null;
        }

        return new OrderPricingResponse(
            order.Subtotal,
            order.DiscountTotal,
            order.ShippingTotal,
            order.TaxTotal,
            order.CouponCode,
            [.. order.Lines.Select(line => new OrderLineResponse(
                line.Sku,
                line.ProductName,
                line.Quantity,
                line.UnitPrice,
                line.LineSubtotal,
                line.LineDiscount,
                line.LineTotal))]);
    }

    private static OrderResponse ToResponse(
        CachedOrder order,
        string correlationId,
        string instanceId)
    {
        return new OrderResponse(
            order.Id,
            order.CustomerId,
            order.Amount,
            order.Currency,
            order.Status,
            order.CreatedAt,
            correlationId,
            instanceId);
    }
}
