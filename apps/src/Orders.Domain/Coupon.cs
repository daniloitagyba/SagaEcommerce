namespace Orders.Domain;

/// <summary>A coupon with tracked redemption state, enforced under concurrency via an atomic guarded UPDATE.</summary>
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

    /// <summary>Null means unlimited redemptions.</summary>
    public int? MaxTotalRedemptions { get; private set; }

    public int? MaxPerCustomer { get; private set; }

    /// <summary>Counts reservations, not completions; a slot is taken the moment a checkout claims it.</summary>
    public int RedemptionCount { get; private set; }

    /// <summary>Coupons sharing a group do not stack with each other or any other promotion in the group.</summary>
    public string? ExclusivityGroup { get; private set; }

    public static Coupon Create(
        string code,
        string description,
        decimal percentage,
        DateTimeOffset validFrom,
        DateTimeOffset validUntil,
        decimal minimumOrderAmount = 0m,
        int? maxTotalRedemptions = null,
        int? maxPerCustomer = null,
        string? exclusivityGroup = null)
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
            RedemptionCount = 0,
            ExclusivityGroup = exclusivityGroup
        };
    }
}

/// <summary>One checkout's claim on a coupon, reserved at checkout and confirmed or released as the order settles.</summary>
public sealed class CouponRedemption
{
    private CouponRedemption()
    {
    }

    public Guid Id { get; private set; }

    public string Code { get; private set; } = string.Empty;

    /// <summary>Unique: an order redeems a given coupon at most once.</summary>
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

/// <summary>A point-in-time snapshot of a coupon as checkout reads it, so eligibility cannot shift mid-decision.</summary>
public sealed record CouponSnapshot(
    string Code,
    string Description,
    decimal Percentage,
    DateTimeOffset ValidFrom,
    DateTimeOffset ValidUntil,
    decimal MinimumOrderAmount,
    int? MaxTotalRedemptions,
    int? MaxPerCustomer,
    int RedemptionCount,
    string? ExclusivityGroup = null);

public static class CouponEligibility
{
    /// <summary>Pure, advisory eligibility check run before pricing; the actual reservation still guards atomically.</summary>
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
