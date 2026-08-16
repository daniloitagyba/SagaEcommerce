using BuildingBlocks;
using Orders.Domain;

namespace Orders.Application.Ports;

public interface IOrderRepository
{
    Task<Order?> FindByIdAsync(Guid id, CancellationToken cancellationToken);
}

/// <summary>
/// Write-side contract for order creation. Kept separate from
/// IOrderRepository so read/status use cases do not depend on idempotency or
/// outbox persistence concerns they never use.
/// </summary>
public interface IOrderCreationRepository : IOrderRepository
{
    /// <summary>
    /// Persists the order, its outbox event and - when the checkout used a
    /// coupon and/or qualified for a budget-limited campaign - their
    /// redemption/budget claims, in one transaction. See
    /// <see cref="CouponReservation"/> and <see cref="CampaignReservation"/>
    /// for why neither claim can be a separate call.
    /// </summary>
    Task<OrderWriteResult> AddAsync(
        Order order,
        OutboxMessage outboxMessage,
        CouponReservation? couponReservation,
        OrderIdempotencyClaim? idempotencyClaim,
        CancellationToken cancellationToken,
        CampaignReservation? campaignReservation = null);

    Task<OrderIdempotencyEntry?> FindIdempotencyAsync(
        string customerId,
        string idempotencyKey,
        CancellationToken cancellationToken);
}

public sealed record OrderIdempotencyClaim(
    string CustomerId,
    string IdempotencyKey,
    string RequestHash,
    Guid OrderId,
    DateTimeOffset CreatedAt);

public sealed record OrderIdempotencyEntry(Guid OrderId, string RequestHash);

public enum OrderWriteOutcome
{
    Created,
    Replayed,
    IdempotencyConflict
}

public sealed record OrderWriteResult(
    OrderWriteOutcome Outcome,
    Guid OrderId,
    string? ExistingRequestHash = null);
