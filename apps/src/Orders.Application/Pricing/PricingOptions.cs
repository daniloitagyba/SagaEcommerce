namespace Orders.Application.Pricing;

/// <summary>
/// Milestone 66: the promotion policy, kept in configuration rather than
/// compiled into the rules so a campaign can be changed without a
/// redeploy. The <em>shape</em> of each promotion is a rule (code); which
/// coupon codes exist and what they are worth is data.
/// </summary>
public sealed class PricingOptions
{
    public const string SectionName = "Pricing";

    // Milestone 67 removed the Coupons dictionary that used to live here.
    // Coupons are no longer configuration: they are rows in the `coupons`
    // table with validity windows and redemption limits, because a coupon
    // that config alone describes has no way to ever be *used up* - which
    // is exactly the defect that milestone fixed.

    /// <summary>Category slug to percentage off that category's lines.</summary>
    public Dictionary<string, decimal> CategoryDiscounts { get; init; } = new(StringComparer.OrdinalIgnoreCase)
    {
        ["electronics"] = 5m
    };

    /// <summary>Buying this many units of a single SKU discounts that line.</summary>
    public int BulkQuantityThreshold { get; init; } = 5;

    public decimal BulkDiscountPercentage { get; init; } = 8m;

    /// <summary>
    /// Milestone 71: shipping cost by zone, keyed on the first two digits
    /// of the CEP. A flat national rate was fine while there was no
    /// address; with one, pretending the Amazon costs the same as the next
    /// suburb is a choice rather than an omission.
    /// </summary>
    public Dictionary<string, decimal> ShippingByPostalPrefix { get; init; } = new(StringComparer.Ordinal)
    {
        ["01"] = 14.90m, ["02"] = 14.90m, ["03"] = 14.90m, ["04"] = 14.90m, ["05"] = 14.90m,
        ["20"] = 19.90m, ["21"] = 19.90m, ["22"] = 19.90m,
        ["30"] = 24.90m, ["40"] = 29.90m,
        ["66"] = 49.90m, ["69"] = 59.90m
    };

    /// <summary>Charged when the destination is outside every known zone.</summary>
    public decimal DefaultShippingAmount { get; init; } = 34.90m;

    /// <summary>
    /// Milestone 71: tax rate by region. Replaces the single global
    /// TaxRatePercentage, which had no way to express that the rate depends
    /// on where the goods land.
    /// </summary>
    public Dictionary<string, decimal> TaxRateByRegion { get; init; } = new(StringComparer.OrdinalIgnoreCase)
    {
        ["SP"] = 18m, ["RJ"] = 20m, ["MG"] = 18m, ["BA"] = 19m, ["PA"] = 17m, ["AM"] = 20m
    };

    /// <summary>Orders at or above this subtotal ship free.</summary>
    public decimal FreeShippingThreshold { get; init; } = 200m;

    public decimal FlatShippingAmount { get; init; } = 19.90m;

    /// <summary>Applied to the discounted subtotal, not the gross subtotal.</summary>
    public decimal TaxRatePercentage { get; init; } = 0m;
}
