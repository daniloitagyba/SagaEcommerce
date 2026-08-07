using Microsoft.Extensions.Options;
using NodaMoney;
using Orders.Application.Pricing;
using Orders.Domain.Pricing;

namespace Orders.UnitTests;

/// <summary>
/// Milestone 66: worked examples for the promotion rules. The invariants
/// that must hold for <em>every</em> input live in
/// PricingEnginePropertyTests - these pin the specific numbers a human
/// would check on a receipt.
/// </summary>
public class PricingEngineTests
{
    private static readonly Currency Brl = Currency.FromCode("BRL");

    private static NRulesPricingEngine BuildEngine(PricingOptions? options = null) =>
        new(Options.Create(options ?? new PricingOptions()));

    private static PricingLine Line(string sku, string category, int quantity, decimal unitPrice) =>
        new(sku, $"Product {sku}", category, quantity, new Money(unitPrice, Brl));

    // Milestone 67: the engine receives a coupon already resolved and eligible (see CouponEligibility), so tests supply the resolved fact, not a code.
    private static PricingRequest Request(decimal? couponPercentage, params PricingLine[] lines) =>
        new("customer-1", Brl, lines,
            couponPercentage is { } percentage
                ? new ResolvedCoupon("SAVE", $"{percentage:0.##}% coupon", percentage)
                : null);

    [Fact]
    public void PricesAPlainOrderWithNoPromotions()
    {
        // 2 x 30.00 = 60.00, below the 200.00 free-shipping threshold.
        var breakdown = BuildEngine().Price(Request(null, Line("SKU-BOOK-001", "books", 2, 30m)));

        Assert.Equal(new Money(60m, Brl), breakdown.Subtotal);
        Assert.Empty(breakdown.Discounts);
        Assert.Equal(new Money(0m, Brl), breakdown.DiscountTotal);
        Assert.Equal(new Money(19.90m, Brl), breakdown.ShippingTotal);
        Assert.Equal(new Money(79.90m, Brl), breakdown.GrandTotal);
    }

    [Fact]
    public void AppliesAPercentageCoupon()
    {
        var breakdown = BuildEngine().Price(Request(10m, Line("SKU-BOOK-001", "books", 1, 100m)));

        var coupon = Assert.Single(breakdown.Discounts);
        Assert.Equal("SAVE", coupon.Code);
        Assert.Equal(new Money(10m, Brl), coupon.Amount);
        Assert.Equal(new Money(109.90m, Brl), breakdown.GrandTotal);
    }

    [Fact]
    public void PricesWithoutACouponWhenNoneWasResolved()
    {
        // Milestone 67 moved coupon validity into CouponEligibility, which runs before pricing - see CouponEligibilityTests for the rejection cases.
        var breakdown = BuildEngine().Price(Request(null, Line("SKU-BOOK-001", "books", 1, 100m)));

        Assert.Empty(breakdown.Discounts);
        Assert.Equal(new Money(100m, Brl), breakdown.Subtotal);
    }

    [Fact]
    public void StacksACouponWithACategoryPromotion()
    {
        // Electronics carry a standing 5% promotion, the coupon is 10% of the whole order - two independent rules the engine combines.
        var breakdown = BuildEngine().Price(Request(
            10m,
            Line("SKU-ELEC-001", "electronics", 1, 1_000m),
            Line("SKU-BOOK-001", "books", 2, 50m)));

        Assert.Equal(new Money(1_100m, Brl), breakdown.Subtotal);
        Assert.Equal(2, breakdown.Discounts.Count);
        Assert.Contains(breakdown.Discounts, d => d.Code == "SAVE" && d.Amount == new Money(110m, Brl));
        Assert.Contains(breakdown.Discounts, d => d.Code == "CATEGORY-ELECTRONICS" && d.Amount == new Money(50m, Brl));
        Assert.Equal(new Money(160m, Brl), breakdown.DiscountTotal);
        // Subtotal cleared the free-shipping threshold, so nothing is added.
        Assert.Equal(new Money(0m, Brl), breakdown.ShippingTotal);
        Assert.Equal(new Money(940m, Brl), breakdown.GrandTotal);
    }

    [Fact]
    public void AppliesAVolumeDiscountPerLine()
    {
        var breakdown = BuildEngine().Price(Request(null, Line("SKU-BOOK-001", "books", 5, 10m)));

        var bulk = Assert.Single(breakdown.Discounts);
        Assert.Equal("BULK-SKU-BOOK-001", bulk.Code);
        Assert.Equal(new Money(4m, Brl), bulk.Amount);
    }

    [Fact]
    public void GrantsFreeShippingOnTheGrossSubtotalSoACouponCannotRevokeIt()
    {
        // 250.00 clears the 200.00 threshold; the 50% coupon drops the
        // payable amount to 125.00, which does not - and shipping stays free.
        var breakdown = BuildEngine().Price(Request(50m, Line("SKU-ELEC-002", "audio", 1, 250m)));

        Assert.Equal(new Money(0m, Brl), breakdown.ShippingTotal);
        Assert.Equal(new Money(125m, Brl), breakdown.GrandTotal);
    }

    [Fact]
    public void CapsStackedDiscountsAtTheSubtotalSoTheTotalNeverGoesNegative()
    {
        // A 50% coupon and an 80% category promotion, each sane alone, together exceed the order's value.
        var options = new PricingOptions
        {
            CategoryDiscounts = new Dictionary<string, decimal>(StringComparer.OrdinalIgnoreCase) { ["clearance"] = 80m },
            FlatShippingAmount = 0m
        };

        var breakdown = BuildEngine(options).Price(Request(50m, Line("SKU-CLTH-001", "clearance", 1, 100m)));

        Assert.Equal(new Money(100m, Brl), breakdown.DiscountTotal);
        Assert.Equal(new Money(0m, Brl), breakdown.GrandTotal);
        // The itemised discounts must still add up to what was applied.
        Assert.Equal(
            breakdown.DiscountTotal,
            breakdown.Discounts.Aggregate(new Money(0m, Brl), (running, d) => running + d.Amount));
    }

    [Fact]
    public void AppliesTaxToTheDiscountedSubtotalNotTheGrossOne()
    {
        var options = new PricingOptions { TaxRatePercentage = 10m, FlatShippingAmount = 0m };

        var breakdown = BuildEngine(options).Price(Request(10m, Line("SKU-BOOK-001", "books", 1, 100m)));

        // 100.00 - 10.00 = 90.00 taxable, not 100.00.
        Assert.Equal(new Money(9m, Brl), breakdown.TaxTotal);
        Assert.Equal(new Money(99m, Brl), breakdown.GrandTotal);
    }

    [Fact]
    public void SplitsTheDiscountAcrossLinesWithoutLosingACentavo()
    {
        // 10.00 of discount over three equal lines is the textbook case
        // where naive per-line rounding loses a centavo.
        var options = new PricingOptions { FlatShippingAmount = 0m };

        var breakdown = BuildEngine(options).Price(Request(
            10m,
            Line("SKU-A", "books", 1, 33.34m),
            Line("SKU-B", "books", 1, 33.33m),
            Line("SKU-C", "books", 1, 33.33m)));

        Assert.Equal(new Money(100m, Brl), breakdown.Subtotal);
        Assert.Equal(new Money(10m, Brl), breakdown.DiscountTotal);
        Assert.Equal(
            breakdown.DiscountTotal,
            breakdown.LineDiscounts.Aggregate(new Money(0m, Brl), (running, share) => running + share));
    }

    // Milestone 66's property tests found this via 10,000 random orders
    // (CsCheck seed 53LlaLK3rYz2): NRules refuses to insert a fact that
    // already compares equal to one in working memory. Before PricingLine
    // and AppliedDiscount switched to identity equality, either test below
    // crashed pricing outright - a real cart shape, not a contrived one.

    [Fact]
    public void TwoIdenticalLinesPriceIndependentlyRatherThanCrashing()
    {
        // Same SKU, quantity and price as two separate lines - e.g. a cart that never merged a duplicate add.
        var breakdown = BuildEngine().Price(Request(
            null,
            Line("SKU-025", "clearance", 10, 100.00m),
            Line("SKU-025", "clearance", 10, 100.00m)));

        Assert.Equal(new Money(2000m, Brl), breakdown.Subtotal);
        Assert.Equal(2, breakdown.LineDiscounts.Count);
    }

    [Fact]
    public void TwoBulkDiscountsThatRoundToTheSameAmountBothApply()
    {
        // Same SKU/quantity, unit prices one centavo apart, so the 8% bulk
        // discount rounds to the same centavo for both lines (182.736 and
        // 182.744 both -> 182.74). Two identical-looking AppliedDiscount
        // facts are still two separate grants, one per line.
        var options = new PricingOptions { BulkQuantityThreshold = 5, BulkDiscountPercentage = 8m, FlatShippingAmount = 0m };

        var breakdown = BuildEngine(options).Price(Request(
            null,
            Line("SKU-025", "clearance", 10, 228.42m),
            Line("SKU-025", "clearance", 10, 228.43m)));

        Assert.Equal(2, breakdown.Discounts.Count);
        Assert.Equal(new Money(365.48m, Brl), breakdown.DiscountTotal);
        Assert.Equal(
            breakdown.DiscountTotal,
            breakdown.Discounts.Aggregate(new Money(0m, Brl), (running, d) => running + d.Amount));
    }
}
