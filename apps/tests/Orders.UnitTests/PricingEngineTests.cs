using Microsoft.Extensions.Options;
using NodaMoney;
using Orders.Application.Pricing;
using Orders.Domain.Pricing;

namespace Orders.UnitTests;

/// <summary>
/// Worked examples for the promotion rules. The invariants
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

    // The engine receives a coupon already resolved and eligible (see CouponEligibility), so tests supply the resolved fact, not a code.
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
        // Coupon validity moved into CouponEligibility, which runs before pricing - see CouponEligibilityTests for the rejection cases.
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

    /// <summary>
    /// Regression coverage for
    /// docs/architecture/audit-2026-08-15-domain-and-business-rules-review.md's
    /// finding 4: when the cap binds, the shopper-presented coupon
    /// ("SAVE") must survive at full value and the automatic category
    /// promotion ("CATEGORY-CLEARANCE") must absorb the truncation - not
    /// the other way around, which is what CapDiscounts' old
    /// alphabetical-by-code walk order would have picked here purely
    /// because "CATEGORY-" sorts before "SAVE".
    /// </summary>
    [Fact]
    public void WhenTheCapBindsTheShopperPresentedCouponSurvivesInFullAndTheAutomaticDiscountAbsorbsTheTruncation()
    {
        var options = new PricingOptions
        {
            CategoryDiscounts = new Dictionary<string, decimal>(StringComparer.OrdinalIgnoreCase) { ["clearance"] = 80m },
            FlatShippingAmount = 0m
        };

        var breakdown = BuildEngine(options).Price(Request(50m, Line("SKU-CLTH-001", "clearance", 1, 100m)));

        Assert.Equal(new Money(100m, Brl), breakdown.DiscountTotal);

        var coupon = Assert.Single(breakdown.Discounts, d => d.Code == "SAVE");
        Assert.Equal(new Money(50m, Brl), coupon.Amount);

        var category = Assert.Single(breakdown.Discounts, d => d.Code == "CATEGORY-CLEARANCE");
        Assert.Equal(new Money(50m, Brl), category.Amount);
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

    // The property tests found this via 10,000 random orders
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

    // LineTaxes - the per-line share of TaxTotal a return
    // refunds alongside LineDiscounts' goods share.

    [Fact]
    public void TaxIsProratedAcrossLinesByTheirDiscountedValue()
    {
        // No coupon, no per-line discount: tax follows raw subtotal 2:1.
        var options = new PricingOptions { TaxRatePercentage = 10m, FlatShippingAmount = 0m };

        var breakdown = BuildEngine(options).Price(Request(
            null,
            Line("SKU-A", "books", 1, 200m),
            Line("SKU-B", "books", 1, 100m)));

        Assert.Equal(new Money(30m, Brl), breakdown.TaxTotal);
        Assert.Equal(new Money(20m, Brl), breakdown.LineTaxes[0]);
        Assert.Equal(new Money(10m, Brl), breakdown.LineTaxes[1]);
        Assert.Equal(breakdown.TaxTotal, breakdown.LineTaxes.Aggregate(new Money(0m, Brl), (running, tax) => running + tax));
    }

    [Fact]
    public void ADiscountFallsProportionallyAcrossLinesByRawSubtotalSoEqualSizedLinesSplitTaxEvenlyEvenUnderATargetedPromotion()
    {
        // AllocateDiscounts spreads the order-level discount
        // total by each line's raw subtotal, regardless of which rule
        // actually granted it - a category promotion that only matched the
        // electronics line still shows up as an even split here, because
        // both lines carry the same subtotal. This is the existing
        // discount-allocation behaviour, not new here; the
        // test exists so a change to that allocation is forced to notice
        // it changes tax refunds on returns too, not just discount receipts.
        var options = new PricingOptions
        {
            TaxRatePercentage = 10m,
            FlatShippingAmount = 0m,
            CategoryDiscounts = new Dictionary<string, decimal>(StringComparer.OrdinalIgnoreCase) { ["electronics"] = 50m }
        };

        var breakdown = BuildEngine(options).Price(Request(
            null,
            Line("SKU-ELEC", "electronics", 1, 100m),
            Line("SKU-BOOK", "books", 1, 100m)));

        Assert.Equal(new Money(15m, Brl), breakdown.TaxTotal);
        Assert.Equal(new Money(7.50m, Brl), breakdown.LineTaxes[0]);
        Assert.Equal(new Money(7.50m, Brl), breakdown.LineTaxes[1]);
    }

    [Fact]
    public void NoTaxRateMeansEveryLinesTaxIsZero()
    {
        var breakdown = BuildEngine().Price(Request(null, Line("SKU-A", "books", 3, 40m)));

        Assert.Equal(new Money(0m, Brl), breakdown.TaxTotal);
        Assert.Equal(new Money(0m, Brl), Assert.Single(breakdown.LineTaxes));
    }

    // Milestone 90: validity windows, exclusivity groups, and campaigns.

    [Fact]
    public void ACategoryPromotionOutsideItsWindowDoesNotFire()
    {
        var now = new DateTimeOffset(2026, 6, 15, 0, 0, 0, TimeSpan.Zero);
        var options = new PricingOptions
        {
            FlatShippingAmount = 0m,
            CategoryDiscounts = new Dictionary<string, decimal>(StringComparer.OrdinalIgnoreCase) { ["electronics"] = 10m },
            // The campaign already ended a month before "now".
            CategoryDiscountWindow = new PromotionWindow(now.AddMonths(-3), now.AddMonths(-1))
        };

        var breakdown = BuildEngine(options).Price(new PricingRequest(
            "customer-1", Brl, [Line("SKU-ELEC", "electronics", 1, 100m)], EvaluatedAt: now));

        Assert.Empty(breakdown.Discounts);
    }

    [Fact]
    public void ACategoryPromotionInsideItsWindowFires()
    {
        var now = new DateTimeOffset(2026, 6, 15, 0, 0, 0, TimeSpan.Zero);
        var options = new PricingOptions
        {
            FlatShippingAmount = 0m,
            CategoryDiscounts = new Dictionary<string, decimal>(StringComparer.OrdinalIgnoreCase) { ["electronics"] = 10m },
            CategoryDiscountWindow = new PromotionWindow(now.AddDays(-1), now.AddDays(1))
        };

        var breakdown = BuildEngine(options).Price(new PricingRequest(
            "customer-1", Brl, [Line("SKU-ELEC", "electronics", 1, 100m)], EvaluatedAt: now));

        var discount = Assert.Single(breakdown.Discounts);
        Assert.Equal("CATEGORY-ELECTRONICS", discount.Code);
        Assert.Equal(new Money(10m, Brl), discount.Amount);
    }

    [Fact]
    public void TwoDiscountsInTheSameExclusivityGroupOnlyTheBiggerSurvives()
    {
        // Category (10% of 100 = 10) and bulk (20% of 100 = 20) both fire on
        // the same line and share a group - only the bulk discount, the
        // larger one, should survive into the breakdown.
        var options = new PricingOptions
        {
            FlatShippingAmount = 0m,
            CategoryDiscounts = new Dictionary<string, decimal>(StringComparer.OrdinalIgnoreCase) { ["electronics"] = 10m },
            CategoryDiscountWindow = new PromotionWindow(null, null, "SEASONAL"),
            BulkQuantityThreshold = 1,
            BulkDiscountPercentage = 20m,
            BulkDiscountWindow = new PromotionWindow(null, null, "SEASONAL")
        };

        var breakdown = BuildEngine(options).Price(Request(null, Line("SKU-ELEC", "electronics", 1, 100m)));

        var discount = Assert.Single(breakdown.Discounts);
        Assert.Equal("BULK-SKU-ELEC", discount.Code);
        Assert.Equal(new Money(20m, Brl), discount.Amount);
        Assert.Equal(new Money(20m, Brl), breakdown.DiscountTotal);
    }

    [Fact]
    public void DiscountsInDifferentGroupsOrWithNoGroupAllStack()
    {
        // Tier (ungrouped) always stacks with everything - exclusivity only
        // ever removes discounts that share a group with another.
        var options = new PricingOptions
        {
            FlatShippingAmount = 0m,
            CategoryDiscounts = new Dictionary<string, decimal>(StringComparer.OrdinalIgnoreCase) { ["electronics"] = 10m },
            CategoryDiscountWindow = new PromotionWindow(null, null, "GROUP-A")
        };
        var request = new PricingRequest(
            "customer-1", Brl, [Line("SKU-ELEC", "electronics", 1, 100m)],
            Customer: new PricingCustomer("customer-1", "Gold", DateTimeOffset.UtcNow.AddYears(-1)));

        var breakdown = BuildEngine(options).Price(request);

        Assert.Equal(2, breakdown.Discounts.Count);
        Assert.Contains(breakdown.Discounts, d => d.Code == "CATEGORY-ELECTRONICS");
        Assert.Contains(breakdown.Discounts, d => d.Code.StartsWith("TIER-", StringComparison.Ordinal));
    }

    [Fact]
    public void AnActiveCampaignAppliesAsAFlatAmountDiscount()
    {
        var options = new PricingOptions { FlatShippingAmount = 0m };
        var request = new PricingRequest(
            "customer-1", Brl, [Line("SKU-A", "books", 1, 100m)],
            Campaign: new ResolvedCampaign("FLASH20", "R$20 off", 20m, null));

        var breakdown = BuildEngine(options).Price(request);

        var discount = Assert.Single(breakdown.Discounts);
        Assert.Equal("FLASH20", discount.Code);
        Assert.Equal(new Money(20m, Brl), discount.Amount);
        Assert.Equal(new Money(20m, Brl), breakdown.DiscountTotal);
        Assert.Equal(new Money(80m, Brl), breakdown.GrandTotal);
    }

    [Fact]
    public void ACampaignNeverDiscountsBelowZero()
    {
        // A flat R$20 campaign on a R$5 order must not send the total negative.
        var options = new PricingOptions { FlatShippingAmount = 0m };
        var request = new PricingRequest(
            "customer-1", Brl, [Line("SKU-A", "books", 1, 5m)],
            Campaign: new ResolvedCampaign("FLASH20", "R$20 off", 20m, null));

        var breakdown = BuildEngine(options).Price(request);

        var discount = Assert.Single(breakdown.Discounts);
        Assert.Equal(new Money(5m, Brl), discount.Amount);
        Assert.Equal(new Money(0m, Brl), breakdown.GrandTotal);
    }

    [Fact]
    public void ACampaignAndACouponInTheSameGroupOnlyTheBiggerSurvives()
    {
        // 15% of 200 = 30, bigger than the flat 20 campaign - the coupon should win.
        var request = new PricingRequest(
            "customer-1", Brl, [Line("SKU-A", "books", 1, 200m)],
            Coupon: new ResolvedCoupon("SAVE15", "15% off", 15m, "SITEWIDE"),
            Campaign: new ResolvedCampaign("FLASH20", "R$20 off", 20m, "SITEWIDE"));

        var breakdown = BuildEngine().Price(request);

        var discount = Assert.Single(breakdown.Discounts);
        Assert.Equal("SAVE15", discount.Code);
        Assert.Equal(new Money(30m, Brl), discount.Amount);
    }
}
