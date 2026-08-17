using Microsoft.Extensions.Options;
using NodaMoney;
using Orders.Application.Pricing;
using Orders.Domain.Pricing;

namespace Orders.UnitTests;

/// <summary>Worked examples for the promotion rules; the invariants that must hold for every input live in PricingEnginePropertyTests - these pin the specific numbers a human would check on a receipt.</summary>
public class PricingEngineTests
{
    private static readonly Currency Brl = Currency.FromCode("BRL");

    private static NRulesPricingEngine BuildEngine(PricingOptions? options = null) =>
        new(Options.Create(options ?? new PricingOptions()));

    private static PricingLine Line(string sku, string category, int quantity, decimal unitPrice) =>
        new(sku, $"Product {sku}", category, quantity, new Money(unitPrice, Brl));

    private static PricingRequest Request(decimal? couponPercentage, params PricingLine[] lines) =>
        new("customer-1", Brl, lines,
            couponPercentage is { } percentage
                ? new ResolvedCoupon("SAVE", $"{percentage:0.##}% coupon", percentage)
                : null);

    [Fact]
    public void PricesAPlainOrderWithNoPromotions()
    {
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
        var breakdown = BuildEngine().Price(Request(null, Line("SKU-BOOK-001", "books", 1, 100m)));

        Assert.Empty(breakdown.Discounts);
        Assert.Equal(new Money(100m, Brl), breakdown.Subtotal);
    }

    [Fact]
    public void StacksACouponWithACategoryPromotion()
    {
        var breakdown = BuildEngine().Price(Request(
            10m,
            Line("SKU-ELEC-001", "electronics", 1, 1_000m),
            Line("SKU-BOOK-001", "books", 2, 50m)));

        Assert.Equal(new Money(1_100m, Brl), breakdown.Subtotal);
        Assert.Equal(2, breakdown.Discounts.Count);
        Assert.Contains(breakdown.Discounts, d => d.Code == "SAVE" && d.Amount == new Money(110m, Brl));
        Assert.Contains(breakdown.Discounts, d => d.Code == "CATEGORY-ELECTRONICS" && d.Amount == new Money(50m, Brl));
        Assert.Equal(new Money(160m, Brl), breakdown.DiscountTotal);
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
        var breakdown = BuildEngine().Price(Request(50m, Line("SKU-ELEC-002", "audio", 1, 250m)));

        Assert.Equal(new Money(0m, Brl), breakdown.ShippingTotal);
        Assert.Equal(new Money(125m, Brl), breakdown.GrandTotal);
    }

    [Fact]
    public void CapsStackedDiscountsAtTheSubtotalSoTheTotalNeverGoesNegative()
    {
        var options = new PricingOptions
        {
            CategoryDiscounts = new Dictionary<string, decimal>(StringComparer.OrdinalIgnoreCase) { ["clearance"] = 80m },
            FlatShippingAmount = 0m
        };

        var breakdown = BuildEngine(options).Price(Request(50m, Line("SKU-CLTH-001", "clearance", 1, 100m)));

        Assert.Equal(new Money(100m, Brl), breakdown.DiscountTotal);
        Assert.Equal(new Money(0m, Brl), breakdown.GrandTotal);
        Assert.Equal(
            breakdown.DiscountTotal,
            breakdown.Discounts.Aggregate(new Money(0m, Brl), (running, d) => running + d.Amount));
    }

    /// <summary>Regression coverage for docs/architecture/audit-2026-08-15-domain-and-business-rules-review.md finding 4: when the cap binds, the shopper-presented coupon survives at full value and the automatic category promotion absorbs the truncation.</summary>
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

        Assert.Equal(new Money(9m, Brl), breakdown.TaxTotal);
        Assert.Equal(new Money(99m, Brl), breakdown.GrandTotal);
    }

    [Fact]
    public void SplitsTheDiscountAcrossLinesWithoutLosingACentavo()
    {
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

    [Fact]
    public void TwoIdenticalLinesPriceIndependentlyRatherThanCrashing()
    {
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

    [Fact]
    public void TaxIsProratedAcrossLinesByTheirDiscountedValue()
    {
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

    [Fact]
    public void ACategoryPromotionOutsideItsWindowDoesNotFire()
    {
        var now = new DateTimeOffset(2026, 6, 15, 0, 0, 0, TimeSpan.Zero);
        var options = new PricingOptions
        {
            FlatShippingAmount = 0m,
            CategoryDiscounts = new Dictionary<string, decimal>(StringComparer.OrdinalIgnoreCase) { ["electronics"] = 10m },
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
