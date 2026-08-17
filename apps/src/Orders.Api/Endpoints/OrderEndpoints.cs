using Orders.Api.Authorization;
using Orders.Api.Contracts;
using Orders.Api.Middleware;
using Orders.Application.Ports;
using Orders.Application.UseCases.CreateOrder;
using Orders.Application.UseCases.GetOrder;
using Orders.Domain;

namespace Orders.Api.Endpoints;

public static class OrderEndpoints
{
    public static IEndpointRouteBuilder MapOrderEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapPost("", CreateAsync).WithTags("Orders").RequireAuthorization(OrdersAuthorizationPolicies.Write);
        endpoints.MapGet("/{id:guid}", GetByIdAsync).WithTags("Orders").RequireAuthorization(OrdersAuthorizationPolicies.Read);

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

        var customerId = httpContext.IsAdmin() ? request.CustomerId : httpContext.GetCustomerId();
        var command = new CreateOrderCommand(
            customerId,
            0m,
            null,
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
        catch (CouponRedemptionUnavailableException exception)
        {
            return Results.Problem(
                detail: exception.Message,
                statusCode: StatusCodes.Status409Conflict,
                title: "Coupon Unavailable");
        }
        catch (CampaignBudgetUnavailableException exception)
        {
            return Results.Problem(
                detail: exception.Message,
                statusCode: StatusCodes.Status409Conflict,
                title: "Campaign Unavailable");
        }

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

        if (result.IdempotencyConflict is { } conflict)
        {
            return Results.Problem(
                detail: $"Idempotency-Key '{conflict.IdempotencyKey}' was already used for a different order request.",
                statusCode: StatusCodes.Status409Conflict,
                title: "Idempotency Key Conflict");
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
        var result = await handler.HandleAsync(id, cancellationToken);

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
                line.LineTotal))],
            order.CampaignCode);
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
