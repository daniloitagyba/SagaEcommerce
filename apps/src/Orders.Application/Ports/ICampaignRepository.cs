using Orders.Domain;

namespace Orders.Application.Ports;

public interface ICampaignRepository
{
    /// <summary>
    /// The best currently-active, still-funded campaign for this subtotal -
    /// "best" meaning highest DiscountAmount, since a shopper should never
    /// see a smaller automatic discount applied when a bigger one also
    /// qualifies. Null when none qualifies. Advisory, the same caveat as
    /// ICouponRepository.FindAsync: the budget it reads can still be taken
    /// by a concurrent checkout before the real reservation closes that
    /// race with an atomic guarded UPDATE.
    /// </summary>
    Task<CampaignSnapshot?> FindBestActiveAsync(
        decimal subtotal,
        DateTimeOffset now,
        CancellationToken cancellationToken);
}

/// <summary>
/// A checkout's claim on a campaign's budget, reserved in the same
/// transaction that persists the order - the same reasoning as
/// CouponReservation: reserving separately and then failing to insert the
/// order would leak budget the release path (keyed off the order reaching
/// Cancelled) could never give back.
/// </summary>
public sealed record CampaignReservation(string Code, decimal Amount, Guid OrderId, DateTimeOffset ReservedAt);

/// <summary>
/// Thrown when the atomic budget claim loses a race it had already passed
/// the advisory eligibility check for - the budget was taken by a
/// concurrent checkout in between. Distinct from a validation failure for
/// the same reason CouponRedemptionUnavailableException is: nothing about
/// the request was wrong, it simply arrived second.
/// </summary>
public sealed class CampaignBudgetUnavailableException(string code, string reason)
    : Exception($"Campaign '{code}' could not be applied: {reason}")
{
    public string Code { get; } = code;
}
