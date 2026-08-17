using BuildingBlocks;

namespace Orders.Application.Ports;

public enum OrderTransitionOutcome
{
    Advanced,
    NotApplicable,
    NotFound
}

public sealed record OrderTransition(OrderTransitionOutcome Outcome, string? PaymentMethod);

public interface IOrderStatusRepository
{
    /// <summary>
    /// Transitions an order to the target status.
    /// </summary>
    Task<OrderTransition> TryTransitionAsync(
        Guid orderId,
        string targetStatus,
        IReadOnlyList<string> allowedFrom,
        OrderSettlementAction settlementAction,
        string correlationId,
        CancellationToken cancellationToken);
}
