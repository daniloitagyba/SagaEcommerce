using BuildingBlocks;
using Orders.Domain;

namespace Orders.Application.Ports;

public interface IOrderRepository
{
    Task<Order?> FindByIdAsync(Guid id, CancellationToken cancellationToken);
}

public interface IOrderCreationRepository : IOrderRepository
{
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
