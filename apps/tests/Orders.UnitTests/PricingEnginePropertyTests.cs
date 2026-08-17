using CsCheck;
using Microsoft.Extensions.Options;
using NodaMoney;
using Orders.Application.Pricing;
using Orders.Domain.Pricing;

namespace Orders.UnitTests;

/// <summary>The pricing invariants, checked against generated orders rather than hand-picked ones; CsCheck generates combinations and shrinks a failure to the smallest order that still breaks it.</summary>
public class PricingEnginePropertyTests
{
    private static readonly Currency Brl = Currency.FromCode("BRL");
    private static readonly string[] Categories = ["electronics", "books", "clothing", "home", "clearance"];
    private static readonly ResolvedCoupon?[] Coupons =
    [
        null,
        new ResolvedCoupon("SAVE10", "10% coupon", 10m),
        new ResolvedCoupon("SAVE20", "20% coupon", 20m),
        new ResolvedCoupon("HALFOFF", "50% coupon", 50m)
    ];

    private static readonly Gen<PricingLine> GenLine =
        from skuIndex in Gen.Int[1, 40]
        from category in Gen.OneOfConst(Categories)
        from quantity in Gen.Int[1, 12]
        from cents in Gen.Int[1, 500_00]
        select new PricingLine(
            $"SKU-{skuIndex:000}",
            $"Product {skuIndex:000}",
            category,
            quantity,
            new Money(cents / 100m, Brl));

    private static readonly Gen<PricingRequest> GenRequest =
        from lines in GenLine.List[1, 6]
        from coupon in Gen.OneOfConst(Coupons)
        select new PricingRequest("customer-1", Brl, lines, coupon);

    private static readonly NRulesPricingEngine Engine = new(Options.Create(new PricingOptions()));

    private static Money Zero => new(0m, Brl);

    private static Money Sum(IEnumerable<Money> amounts) =>
        amounts.Aggregate(Zero, (running, amount) => running + amount);

    [Fact]
    public void GrandTotalIsNeverNegative()
    {
        GenRequest.Sample(
            request => Engine.Price(request).GrandTotal >= Zero,
            iter: 10_000);
    }

    [Fact]
    public void DiscountNeverExceedsSubtotal()
    {
        GenRequest.Sample(
            request =>
            {
                var breakdown = Engine.Price(request);
                return breakdown.DiscountTotal <= breakdown.Subtotal;
            },
            iter: 10_000);
    }

    [Fact]
    public void PerLineDiscountsSumToExactlyTheOrderDiscount()
    {
        GenRequest.Sample(
            request =>
            {
                var breakdown = Engine.Price(request);
                return Sum(breakdown.LineDiscounts) == breakdown.DiscountTotal;
            },
            iter: 10_000);
    }

    [Fact]
    public void ItemisedDiscountsSumToTheDiscountTotal()
    {
        GenRequest.Sample(
            request =>
            {
                var breakdown = Engine.Price(request);
                return Sum(breakdown.Discounts.Select(discount => discount.Amount)) == breakdown.DiscountTotal;
            },
            iter: 10_000);
    }

    [Fact]
    public void GrandTotalAlwaysEqualsItsParts()
    {
        GenRequest.Sample(
            request =>
            {
                var breakdown = Engine.Price(request);
                var expected = breakdown.Subtotal - breakdown.DiscountTotal + breakdown.ShippingTotal + breakdown.TaxTotal;
                return breakdown.GrandTotal == expected;
            },
            iter: 10_000);
    }

    [Fact]
    public void SubtotalAlwaysEqualsTheSumOfTheLines()
    {
        GenRequest.Sample(
            request =>
            {
                var breakdown = Engine.Price(request);
                return breakdown.Subtotal == Sum(request.Lines.Select(line => line.LineSubtotal));
            },
            iter: 10_000);
    }

    [Fact]
    public void ThereIsOneLineDiscountShareForEveryLine()
    {
        GenRequest.Sample(
            request => Engine.Price(request).LineDiscounts.Count == request.Lines.Count,
            iter: 10_000);
    }

    [Fact]
    public void PricingIsDeterministic()
    {
        GenRequest.Sample(
            request =>
            {
                var first = Engine.Price(request);
                var second = Engine.Price(request);
                return first.GrandTotal == second.GrandTotal
                    && first.DiscountTotal == second.DiscountTotal
                    && first.Discounts.Count == second.Discounts.Count;
            },
            iter: 2_000);
    }

    [Fact]
    public void PresentingACouponNeverCostsTheShopperMore()
    {
        var gen =
            from lines in GenLine.List[1, 6]
            from coupon in Gen.OneOfConst(
                new ResolvedCoupon("SAVE10", "10% coupon", 10m),
                new ResolvedCoupon("SAVE20", "20% coupon", 20m),
                new ResolvedCoupon("HALFOFF", "50% coupon", 50m))
            select (lines, coupon);

        gen.Sample(
            input =>
            {
                var without = Engine.Price(new PricingRequest("customer-1", Brl, input.lines));
                var with = Engine.Price(new PricingRequest("customer-1", Brl, input.lines, input.coupon));
                return with.GrandTotal <= without.GrandTotal;
            },
            iter: 10_000);
    }

    [Fact]
    public void NoLineEverReceivesANegativeDiscountShare()
    {
        GenRequest.Sample(
            request =>
            {
                var breakdown = Engine.Price(request);
                return breakdown.LineDiscounts.All(share => share >= Zero)
                    && breakdown.Discounts.All(discount => discount.Amount >= Zero)
                    && breakdown.ShippingTotal >= Zero
                    && breakdown.TaxTotal >= Zero;
            },
            iter: 10_000);
    }

    [Fact]
    public void NoLineIsEverDiscountedBelowFree()
    {
        GenRequest.Sample(
            request =>
            {
                var breakdown = Engine.Price(request);
                return breakdown.LineDiscounts
                    .Zip(request.Lines, (share, line) => share <= line.LineSubtotal)
                    .All(withinLine => withinLine);
            },
            iter: 10_000);
    }

    [Fact]
    public void EveryMoneyAmountIsRoundedToTheCurrencysMinorUnit()
    {
        GenRequest.Sample(
            request =>
            {
                var breakdown = Engine.Price(request);
                return IsWholeCentavos(breakdown.GrandTotal)
                    && IsWholeCentavos(breakdown.DiscountTotal)
                    && IsWholeCentavos(breakdown.ShippingTotal)
                    && IsWholeCentavos(breakdown.TaxTotal)
                    && breakdown.LineDiscounts.All(IsWholeCentavos);
            },
            iter: 10_000);
    }

    private static bool IsWholeCentavos(Money money) =>
        decimal.Round(money.Amount, 2) == money.Amount;
}
