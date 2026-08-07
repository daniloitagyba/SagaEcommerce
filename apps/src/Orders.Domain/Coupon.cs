namespace Orders.Domain;

/// <summary>
/// Milestone 67: a coupon with a life, replacing Milestone 66's
/// configuration dictionary of code -> percentage.
///
/// That dictionary had no notion of a coupon being <em>used</em>. HALFOFF
/// took 50% off, and nothing anywhere counted how many times, by whom, or
/// until when - so a single leaked code could be redeemed indefinitely by
/// anyone. Promotion abuse is the ordinary consequence of that in real
/// storefronts, and it is the one gap in this lab's domain that was an
/// outright defect rather than a missing feature.
///
/// The interesting part is not the columns, though: enforcing
/// MaxTotalRedemptions under concurrent checkout is the same class of
/// distributed problem as reserving stock, and gets the same treatment -
/// an atomic guarded UPDATE rather than read-then-write, and a redemption
/// that participates in the saga so a cancelled order gives its slot back.
/// </summary>
public sealed class Coupon
{
    private Coupon()
    {
    }

    public string Code { get; private set; } = string.Empty;

    public string Description { get; private set; } = string.Empty;

    public decimal Percentage { get; private set; }

    public DateTimeOffset ValidFrom { get; private set; }

    public DateTimeOffset ValidUntil { get; private set; }

    /// <summary>Orders below this subtotal cannot use the coupon at all.</summary>
    public decimal MinimumOrderAmount { get; private set; }

    /// <summary>Null means unlimited - the Milestone 66 behaviour, now opt-in rather than the only option.</summary>
    public int? MaxTotalRedemptions { get; private set; }

    public int? MaxPerCustomer { get; private set; }

    /// <summary>
    /// Counts reservations, not completions: a slot is taken the moment a
    /// checkout claims it and only given back if that order is cancelled.
    /// Counting completions instead would let unlimited concurrent
    /// checkouts all pass the limit check before any of them finished.
    /// </summary>
    public int RedemptionCount { get; private set; }

    public static Coupon Create(
        string code,
        string description,
        decimal percentage,
        DateTimeOffset validFrom,
        DateTimeOffset validUntil,
        decimal minimumOrderAmount = 0m,
        int? maxTotalRedemptions = null,
        int? maxPerCustomer = null)
    {
        return new Coupon
        {
            Code = code.Trim().ToUpperInvariant(),
            Description = description,
            Percentage = percentage,
            ValidFrom = validFrom,
            ValidUntil = validUntil,
            MinimumOrderAmount = minimumOrderAmount,
            MaxTotalRedemptions = maxTotalRedemptions,
            MaxPerCustomer = maxPerCustomer,
            RedemptionCount = 0
        };
    }
}

/// <summary>
/// One checkout's claim on a coupon. Reserved at checkout, then confirmed
/// or released as the order settles - which is what makes a coupon
/// redemption a saga participant rather than a fire-and-forget increment.
/// </summary>
public sealed class CouponRedemption
{
    private CouponRedemption()
    {
    }

    public Guid Id { get; private set; }

    public string Code { get; private set; } = string.Empty;

    /// <summary>Unique: an order redeems a given coupon at most once, which is also what makes retrying a checkout safe.</summary>
    public Guid OrderId { get; private set; }

    public string CustomerId { get; private set; } = string.Empty;

    public string State { get; private set; } = string.Empty;

    public DateTimeOffset ReservedAt { get; private set; }

    public DateTimeOffset? SettledAt { get; private set; }
}

/// <summary>Why a coupon could not be applied - surfaced to the shopper rather than silently ignored.</summary>
public enum CouponRejectionReason
{
    None,
    NotFound,
    NotYetValid,
    Expired,
    BelowMinimumOrderAmount,
    TotalRedemptionLimitReached,
    CustomerRedemptionLimitReached
}

/// <summary>
/// A coupon as the checkout reads it - a point-in-time snapshot, not a
/// tracked entity, because eligibility has to be decided from values that
/// cannot shift underneath the decision.
/// </summary>
public sealed record CouponSnapshot(
    string Code,
    string Description,
    decimal Percentage,
    DateTimeOffset ValidFrom,
    DateTimeOffset ValidUntil,
    decimal MinimumOrderAmount,
    int? MaxTotalRedemptions,
    int? MaxPerCustomer,
    int RedemptionCount);

public static class CouponEligibility
{
    /// <summary>
    /// Pure, so it can be reasoned about and property-tested without a
    /// database - the same rule the pricing engine already follows.
    ///
    /// This is an advisory check: it runs before the order is priced, and
    /// the limits it reads can be taken by a concurrent checkout between
    /// here and the actual reservation. That race is closed at reservation
    /// time by an atomic guarded UPDATE, not here. Checking twice is
    /// deliberate - this pass exists to give the shopper a specific reason
    /// ("expired", "below the minimum") instead of a bare failure, and to
    /// avoid claiming a redemption slot for an order that was never going
    /// to qualify.
    /// </summary>
    public static CouponRejectionReason Evaluate(
        CouponSnapshot? coupon,
        decimal subtotal,
        int customerRedemptionCount,
        DateTimeOffset now)
    {
        if (coupon is null)
        {
            return CouponRejectionReason.NotFound;
        }

        if (now < coupon.ValidFrom)
        {
            return CouponRejectionReason.NotYetValid;
        }

        if (now >= coupon.ValidUntil)
        {
            return CouponRejectionReason.Expired;
        }

        if (subtotal < coupon.MinimumOrderAmount)
        {
            return CouponRejectionReason.BelowMinimumOrderAmount;
        }

        if (coupon.MaxTotalRedemptions is { } maxTotal && coupon.RedemptionCount >= maxTotal)
        {
            return CouponRejectionReason.TotalRedemptionLimitReached;
        }

        if (coupon.MaxPerCustomer is { } maxPerCustomer && customerRedemptionCount >= maxPerCustomer)
        {
            return CouponRejectionReason.CustomerRedemptionLimitReached;
        }

        return CouponRejectionReason.None;
    }

    public static string Describe(CouponRejectionReason reason, string code) => reason switch
    {
        CouponRejectionReason.NotFound => $"Coupon '{code}' does not exist.",
        CouponRejectionReason.NotYetValid => $"Coupon '{code}' is not valid yet.",
        CouponRejectionReason.Expired => $"Coupon '{code}' has expired.",
        CouponRejectionReason.BelowMinimumOrderAmount => $"This order does not reach the minimum amount required by coupon '{code}'.",
        CouponRejectionReason.TotalRedemptionLimitReached => $"Coupon '{code}' has reached its redemption limit.",
        CouponRejectionReason.CustomerRedemptionLimitReached => $"You have already redeemed coupon '{code}' the maximum number of times.",
        _ => $"Coupon '{code}' was applied."
    };
}
