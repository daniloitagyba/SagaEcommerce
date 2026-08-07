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

/// <summary>
/// Milestone 67: a checkout's claim on a coupon, reserved in the same
/// transaction that persists the order - it has to be, since reserving
/// separately and then failing to insert the order would leak a slot the
/// release path (keyed off the order reaching Cancelled) could never give back.
/// </summary>
public sealed record CouponReservation(string Code, Guid OrderId, string CustomerId, DateTimeOffset ReservedAt);

/// <summary>
/// Thrown when the atomic reservation loses a race it had already passed
/// the advisory eligibility check for - the limit was taken by a
/// concurrent checkout in between. Distinct from a validation failure
/// because nothing about the request was wrong; it simply arrived second.
/// </summary>
public sealed class CouponRedemptionUnavailableException(string code, string reason)
    : Exception($"Coupon '{code}' could not be redeemed: {reason}")
{
    public string Code { get; } = code;
}
