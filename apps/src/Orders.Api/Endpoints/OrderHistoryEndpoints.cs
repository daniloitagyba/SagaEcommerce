using Orders.Api.Authorization;
using Orders.Api.Contracts;
using Orders.Application.UseCases.GetOrderHistory;

namespace Orders.Api.Endpoints;

/// <summary>The event-sourced read side, folding order state and history from the append-only event log.</summary>
public static class OrderHistoryEndpoints
{
    public static IEndpointRouteBuilder MapOrderHistoryEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/{id:guid}/history", GetHistoryAsync)
            .WithTags("Orders")
            .RequireAuthorization(OrdersAuthorizationPolicies.Read);

        return endpoints;
    }

    private static async Task<IResult> GetHistoryAsync(
        Guid id,
        GetOrderHistoryHandler handler,
        DateTimeOffset? asOf,
        HttpContext httpContext,
        CancellationToken cancellationToken)
    {
        var result = await handler.HandleAsync(id, asOf, cancellationToken);

        if (result.Snapshot is null || !httpContext.MayAccess(result.Snapshot.CustomerId))
        {
            return Results.NotFound();
        }

        var response = new OrderHistoryResponse(
            id,
            new OrderSnapshotResponse(
                result.Snapshot.CustomerId,
                result.Snapshot.Amount,
                result.Snapshot.Currency,
                result.Snapshot.Status,
                result.Snapshot.CreatedAt),
            result.Events
                .Select(item => new OrderEventResponse(item.Id, item.EventType, item.OccurredAt))
                .ToList());

        return Results.Ok(response);
    }
}
