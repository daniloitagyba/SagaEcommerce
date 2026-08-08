using Orders.Api.Authorization;
using Orders.Api.Contracts;
using Orders.Api.RateLimiting;
using Orders.Application.UseCases.GetOrderHistory;

namespace Orders.Api.Endpoints;

/// <summary>
/// The event-sourced read side: state folded from the append-only
/// event log rather than a current-state row, plus the full event history
/// alongside it. Entirely separate from GetByIdAsync's cache-aside read of
/// the orders table - this is a second, independent way to read the same
/// aggregate, built on the event store rather than current state.
/// </summary>
public static class OrderHistoryEndpoints
{
    public static IEndpointRouteBuilder MapOrderHistoryEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/orders/{id:guid}/history", GetHistoryAsync)
            .WithTags("Orders")
            .RequireRateLimiting(RateLimitingExtensions.OrdersPolicy)
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

        // Same 404-hides-ownership reasoning as GetByIdAsync.
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
