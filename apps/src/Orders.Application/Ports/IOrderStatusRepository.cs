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
    /// Moves an order into <paramref name="targetStatus"/>, guarded on the
    /// legal predecessors, and queues the implied settlement command in the
    /// same transaction - atomic, or a capture could outlive a rolled-back "Shipped".
    /// </summary>
    Task<OrderTransition> TryTransitionAsync(
        Guid orderId,
        string targetStatus,
        IReadOnlyList<string> allowedFrom,
        OrderSettlementAction settlementAction,
        string correlationId,
        CancellationToken cancellationToken);
}
