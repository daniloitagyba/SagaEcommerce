using CsCheck;
using Microsoft.Extensions.Options;
using NodaMoney;
using Orders.Application.Pricing;
using Orders.Domain.Pricing;

namespace Orders.UnitTests;

/// <summary>
/// Milestone 66: the pricing invariants, checked against generated orders
/// rather than hand-picked ones - the dangerous cases here are the ones
/// nobody thinks of (campaigns stacking past 100%, a discount that won't
/// divide evenly, a coupon on a zero-value order). CsCheck generates those
/// combinations and shrinks a failure to the smallest order that still
/// breaks it. These properties must hold for <em>any</em> order and
/// promotion combination, no matter what a future campaign does.
/// </summary>
public class PricingEnginePropertyTests
{
    private static readonly Currency Brl = Currency.FromCode("BRL");
    private static readonly string[] Categories = ["electronics", "books", "clothing", "home", "clearance"];
    // Milestone 67: the engine only ever sees already-resolved, eligible
    // coupons, so the generator produces percentages, not codes - see CouponEligibilityTests.
    private static readonly ResolvedCoupon?[] Coupons =
    [
        null,
        new ResolvedCoupon("SAVE10", "10% coupon", 10m),
        new ResolvedCoupon("SAVE20", "20% coupon", 20m),
        new ResolvedCoupon("HALFOFF", "50% coupon", 50m)
    ];

    // Prices and quantities stay in realistic ranges; the *combinations* are left entirely to the generator.
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
        // The invariant that motivated the discount cap: two sane campaigns can jointly exceed the order's value.
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
        // The centavo property - naive proportional rounding fails this whenever a discount doesn't divide evenly across its lines.
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
        // The receipt has to add up to what was actually deducted, even after the cap trims an entry.
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
        // Rule engines evaluate in a non-obvious order; this pins that the *result* doesn't depend on it.
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
        // Monotonicity: adding a valid coupon must never push the total up
        // - catches a future rule that adds a charge off CouponCode, or
        // free shipping computed on the discounted subtotal.
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
        // The property Milestone 66 should have had and did not: NodaMoney's
        // Split emits a negative share for roughly 1 in 200k weighted
        // allocations, which is a line whose discount *raises* its price.
        // Milestone 70 hit the same defect in refunds and replaced Split on
        // both paths; this is the check that would have caught it first.
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
        // A line's discount share must never exceed what that line costs, or its net goes negative.
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
