namespace Orders.Application.Pricing;

/// <summary>
/// An optional validity window and exclusivity group for one of the
/// automatic promotion rules (Milestone 90). Null on a PricingOptions
/// property means "always active, stacks with everything" - today's
/// default and fully backward compatible; setting one turns that
/// promotion into a calendar-gated campaign the way CategoryDiscounts'
/// percentages already turn a category into a discounted one. One window
/// per promotion *type*, not per category/SKU - "electronics get 5% off,
/// and that runs Black Friday through Cyber Monday" is the granularity a
/// campaign calendar actually needs; per-category calendars would be a
/// different feature.
/// </summary>
public sealed record PromotionWindow(
    DateTimeOffset? ValidFrom,
    DateTimeOffset? ValidUntil,
    /// <summary>Promotions sharing a group do not stack - the best-value one wins; see NRulesPricingEngine's exclusivity reduction.</summary>
    string? ExclusivityGroup = null)
{
    public bool IsActive(DateTimeOffset at) =>
        (ValidFrom is null || at >= ValidFrom) && (ValidUntil is null || at < ValidUntil);
}

/// <summary>
/// The promotion policy, kept in configuration rather than
/// compiled into the rules so a campaign can be changed without a
/// redeploy. The <em>shape</em> of each promotion is a rule (code); which
/// coupon codes exist and what they are worth is data.
/// </summary>
public sealed class PricingOptions
{
    public const string SectionName = "Pricing";

    // The Coupons dictionary was removed - coupons are now rows in
    // `coupons` with validity windows and redemption limits, since config alone can't express being *used up*.

    /// <summary>Calendar/exclusivity for CategoryDiscountRule. Null (the default) means always active, no group.</summary>
    public PromotionWindow? CategoryDiscountWindow { get; init; }

    /// <summary>Calendar/exclusivity for BulkQuantityRule.</summary>
    public PromotionWindow? BulkDiscountWindow { get; init; }

    /// <summary>Calendar/exclusivity for LoyaltyTierRule.</summary>
    public PromotionWindow? TierDiscountWindow { get; init; }

    /// <summary>Calendar/exclusivity for FreeShippingRule.</summary>
    public PromotionWindow? FreeShippingWindow { get; init; }

    /// <summary>Category slug to percentage off that category's lines.</summary>
    public Dictionary<string, decimal> CategoryDiscounts { get; init; } = new(StringComparer.OrdinalIgnoreCase)
    {
        ["electronics"] = 5m
    };

    /// <summary>Buying this many units of a single SKU discounts that line.</summary>
    public int BulkQuantityThreshold { get; init; } = 5;

    public decimal BulkDiscountPercentage { get; init; } = 8m;

    /// <summary>Shipping cost by zone, keyed on the CEP's first two digits - a flat rate was fine before there was an address to read.</summary>
    public Dictionary<string, decimal> ShippingByPostalPrefix { get; init; } = new(StringComparer.Ordinal)
    {
        ["01"] = 14.90m,
        ["02"] = 14.90m,
        ["03"] = 14.90m,
        ["04"] = 14.90m,
        ["05"] = 14.90m,
        ["20"] = 19.90m,
        ["21"] = 19.90m,
        ["22"] = 19.90m,
        ["30"] = 24.90m,
        ["40"] = 29.90m,
        ["66"] = 49.90m,
        ["69"] = 59.90m
    };

    /// <summary>Charged when the destination is outside every known zone.</summary>
    public decimal DefaultShippingAmount { get; init; } = 34.90m;

    /// <summary>Tax rate by region, replacing the single global TaxRatePercentage.</summary>
    public Dictionary<string, decimal> TaxRateByRegion { get; init; } = new(StringComparer.OrdinalIgnoreCase)
    {
        ["SP"] = 18m,
        ["RJ"] = 20m,
        ["MG"] = 18m,
        ["BA"] = 19m,
        ["PA"] = 17m,
        ["AM"] = 20m
    };

    /// <summary>Orders at or above this subtotal ship free.</summary>
    public decimal FreeShippingThreshold { get; init; } = 200m;

    public decimal FlatShippingAmount { get; init; } = 19.90m;

    /// <summary>Applied to the discounted subtotal, not the gross subtotal.</summary>
    public decimal TaxRatePercentage { get; init; } = 0m;
}
