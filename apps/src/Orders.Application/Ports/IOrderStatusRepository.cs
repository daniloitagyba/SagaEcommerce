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
    /// legal predecessors, and - in the same transaction - queues whatever
    /// settlement command that status implies.
    ///
    /// Atomic on purpose: a capture command that outlived a rolled-back
    /// "Shipped" would charge a customer for goods that never left.
    /// </summary>
    Task<OrderTransition> TryTransitionAsync(
        Guid orderId,
        string targetStatus,
        IReadOnlyList<string> allowedFrom,
        OrderSettlementAction settlementAction,
        string correlationId,
        CancellationToken cancellationToken);
}
