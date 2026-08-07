using Orders.Api.Authorization;
using Orders.Api.Middleware;
using Orders.Application.Exceptions;
using Orders.Application.UseCases.ReturnOrder;
using Orders.Domain;

namespace Orders.Api.Endpoints;

public sealed record CreateReturnItemRequest(string? Sku, int Quantity);

public sealed record CreateReturnRequest(IReadOnlyList<CreateReturnItemRequest>? Items, string? Reason);

/// <summary>
/// Milestone 70: a customer (or support agent) sending part of a delivered
/// order back.
/// </summary>
public static class ReturnEndpoints
{
    public static IEndpointRouteBuilder MapReturnEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapPost("/orders/{id:guid}/returns", CreateAsync)
            .WithTags("Returns")
            .RequireAuthorization(OrdersAuthorizationPolicies.Write);

        return endpoints;
    }

    private static async Task<IResult> CreateAsync(
        Guid id,
        CreateReturnRequest request,
        ReturnOrderHandler handler,
        HttpContext httpContext,
        CancellationToken cancellationToken)
    {
        if (request.Items is null || request.Items.Count == 0)
        {
            return Results.ValidationProblem(new Dictionary<string, string[]>(StringComparer.Ordinal)
            {
                ["items"] = ["At least one item is required."]
            });
        }

        if (request.Items.Any(item => string.IsNullOrWhiteSpace(item.Sku)))
        {
            return Results.ValidationProblem(new Dictionary<string, string[]>(StringComparer.Ordinal)
            {
                ["items"] = ["Every item must carry a sku."]
            });
        }

        ReturnOrderResult result;
        try
        {
            result = await handler.HandleAsync(
                id,
                [.. request.Items.Select(item => new ReturnItemRequest(item.Sku!, item.Quantity))],
                request.Reason ?? string.Empty,
                httpContext.GetCorrelationId(),
                cancellationToken);
        }
        catch (InfrastructureUnavailableException)
        {
            httpContext.Response.Headers["Retry-After"] = "5";
            return Results.Problem(
                detail: "PostgreSQL is currently unavailable.",
                statusCode: StatusCodes.Status503ServiceUnavailable,
                title: "Service Unavailable");
        }

        if (result.OrderNotFound)
        {
            return Results.NotFound();
        }

        if (result.Rejection != ReturnRejectionReason.None)
        {
            // 409 for a state problem (not delivered, already returned),
            // 400 for a request problem (unknown sku, bad quantity) - the
            // difference is whether resubmitting the same body could ever
            // succeed.
            var isStateProblem = result.Rejection == ReturnRejectionReason.OrderNotDelivered;
            return Results.Problem(
                detail: Describe(result.Rejection, result.OffendingSku),
                statusCode: isStateProblem ? StatusCodes.Status409Conflict : StatusCodes.Status400BadRequest,
                title: "Return Refused");
        }

        return Results.Created($"/orders/{id}/returns/{result.ReturnId}", new
        {
            returnId = result.ReturnId,
            orderId = id,
            refundTotal = result.RefundTotal,
            orderFullyReturned = result.OrderFullyReturned,
            correlationId = httpContext.GetCorrelationId()
        });
    }

    private static string Describe(ReturnRejectionReason reason, string? sku) => reason switch
    {
        ReturnRejectionReason.OrderNotDelivered => "Only a delivered order can be returned.",
        ReturnRejectionReason.UnknownSku => $"SKU '{sku}' is not part of this order.",
        ReturnRejectionReason.QuantityNotPositive => $"The quantity returned for '{sku}' must be greater than zero.",
        ReturnRejectionReason.ExceedsPurchasedQuantity => $"More of '{sku}' was requested than remains returnable on this order.",
        ReturnRejectionReason.NothingToReturn => "At least one item is required.",
        _ => "The return was refused."
    };
}
