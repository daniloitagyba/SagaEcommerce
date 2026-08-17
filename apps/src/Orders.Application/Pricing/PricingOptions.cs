namespace Orders.Application.Pricing;

/// <summary>
/// Configures an automatic promotion rule.
/// </summary>
public sealed record PromotionWindow(
    DateTimeOffset? ValidFrom,
    DateTimeOffset? ValidUntil,
    /// <summary>Promotions sharing a group do not stack; the best-value one wins.</summary>
    string? ExclusivityGroup = null)
{
    public bool IsActive(DateTimeOffset at) =>
        (ValidFrom is null || at >= ValidFrom) && (ValidUntil is null || at < ValidUntil);
}

/// <summary>
/// Configures promotion pricing.
/// </summary>
public sealed class PricingOptions
{
    public const string SectionName = "Pricing";

    /// <summary>Calendar/exclusivity for CategoryDiscountRule; null means always active, no group.</summary>
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

    /// <summary>Shipping cost by zone, keyed on the CEP's first two digits.</summary>
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
