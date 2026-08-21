using Orders.Domain;

namespace Orders.Application.Ports;

public interface ICouponRepository
{
    /// <summary>Reads a coupon and how many times this customer has already redeemed it. Null when the code does not exist.</summary>
    Task<(CouponSnapshot? Coupon, int CustomerRedemptionCount)> FindAsync(
        string code,
        string customerId,
        CancellationToken cancellationToken);
}

/// <summary>A checkout's claim on a coupon, reserved in the same transaction that persists the order.</summary>
public sealed record CouponReservation(string Code, Guid OrderId, string CustomerId, DateTimeOffset ReservedAt);

public sealed class CouponRedemptionUnavailableException(string code, string reason)
    : Exception($"Coupon '{code}' could not be redeemed: {reason}")
{
    public string Code { get; } = code;
}
