using BuildingBlocks;
using Orders.Domain;

namespace Orders.Application.Ports;

public interface IOrderRepository
{
    /// <summary>
    /// Persists the order, its outbox event and - when the checkout used a
    /// coupon - its redemption claim, in one transaction. See
    /// <see cref="CouponReservation"/> for why the claim cannot be a
    /// separate call.
    /// </summary>
    Task AddAsync(
        Order order,
        OutboxMessage outboxMessage,
        CouponReservation? couponReservation,
        CancellationToken cancellationToken);

    Task<Order?> FindByIdAsync(Guid id, CancellationToken cancellationToken);
}
