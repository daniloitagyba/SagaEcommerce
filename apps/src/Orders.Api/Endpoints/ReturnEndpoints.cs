using Orders.Api.Authorization;
using Orders.Api.Middleware;
using Orders.Application.Exceptions;
using Orders.Application.UseCases.ReturnOrder;
using Orders.Domain;

namespace Orders.Api.Endpoints;

public sealed record CreateReturnItemRequest(string? Sku, int Quantity);

/// <summary>A request to return part of a delivered order; ReasonCategory defaults to Unwanted.</summary>
public sealed record CreateReturnRequest(IReadOnlyList<CreateReturnItemRequest>? Items, string? Reason, string? ReasonCategory);

/// <summary>Endpoint for a customer or support agent to return part of a delivered order.</summary>
public static class ReturnEndpoints
{
    public static IEndpointRouteBuilder MapReturnEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapPost("/{id:guid}/returns", CreateAsync)
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

        ReturnReasonCategory reasonCategory = ReturnReasonCategory.Unwanted;
        if (!string.IsNullOrWhiteSpace(request.ReasonCategory)
            && !Enum.TryParse(request.ReasonCategory, ignoreCase: true, out reasonCategory))
        {
            return Results.ValidationProblem(new Dictionary<string, string[]>(StringComparer.Ordinal)
            {
                ["reasonCategory"] = [$"'{request.ReasonCategory}' is not a recognized return reason. Valid values: Defect, Regret, Unwanted."]
            });
        }

        ReturnOrderResult result;
        try
        {
            result = await handler.HandleAsync(
                id,
                [.. request.Items.Select(item => new ReturnItemRequest(item.Sku!, item.Quantity))],
                request.Reason ?? string.Empty,
                reasonCategory,
                httpContext.GetCallerIdentity(),
                httpContext.GetCorrelationId(),
                cancellationToken);
        }
        catch (OrderReturnConflictException)
        {
            return Results.Problem(
                detail: "This order was modified by another request. Retry the return.",
                statusCode: StatusCodes.Status409Conflict,
                title: "Return Conflict");
        }

        if (result.OrderNotFound)
        {
            return Results.NotFound();
        }

        if (result.Rejection != ReturnRejectionReason.None)
        {
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
