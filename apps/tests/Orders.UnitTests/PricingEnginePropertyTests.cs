using CsCheck;
using Microsoft.Extensions.Options;
using NodaMoney;
using Orders.Application.Pricing;
using Orders.Domain.Pricing;

namespace Orders.UnitTests;

/// <summary>
/// Milestone 66: the pricing invariants, checked against generated orders
/// rather than hand-picked ones.
///
/// Every other test in this repository asserts on examples somebody thought
/// of. Pricing is the first piece of logic here where the dangerous cases
/// are the ones nobody thinks of: independently-authored campaigns that
/// happen to stack past 100%, a discount that will not divide evenly across
/// its lines, a coupon on a zero-value order. CsCheck generates those
/// combinations and, when a property fails, shrinks the counterexample down
/// to the smallest order that still breaks it - which is the actual reason
/// to reach for property-based testing over another dozen [Fact]s.
///
/// The properties below are the ones that must hold for <em>any</em> order
/// and any combination of promotions, no matter what a future campaign
/// does. A new rule that violates one of them is a bug in the rule, and
/// this is the test that will say so.
/// </summary>
public class PricingEnginePropertyTests
{
    private static readonly Currency Brl = Currency.FromCode("BRL");
    private static readonly string[] Categories = ["electronics", "books", "clothing", "home", "clearance"];
    // Milestone 67: the engine only ever sees coupons that were already
    // resolved and found eligible, so the generator produces resolved
    // percentages (or none) rather than codes. Whether a code exists, has
    // expired or has run out is decided before pricing - see
    // CouponEligibilityTests.
    private static readonly ResolvedCoupon?[] Coupons =
    [
        null,
        new ResolvedCoupon("SAVE10", "10% coupon", 10m),
        new ResolvedCoupon("SAVE20", "20% coupon", 20m),
        new ResolvedCoupon("HALFOFF", "50% coupon", 50m)
    ];

    // Prices and quantities are generated in the ranges a real storefront
    // sees, but the *combinations* are left entirely to the generator -
    // including the ones that stack several promotions onto one order.
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
        // The invariant that motivated the discount cap: two campaigns can
        // each be individually sane and jointly exceed the order's value.
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
        // The centavo property. Naive proportional rounding fails this for
        // any discount that does not divide evenly across its lines, and
        // the failure is invisible until someone reconciles a refund.
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
        // What the customer sees on the receipt has to add up to what was
        // actually deducted - including after the cap trims an entry.
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
        // Rule engines evaluate in an order that is not obvious from the
        // source; this pins that the *result* does not depend on it.
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
        // Monotonicity: whatever the other campaigns do, adding a valid
        // coupon must not push the total up. This is the property that
        // would catch a future rule keying off CouponCode to *add* a
        // charge, or free shipping being computed on the discounted
        // subtotal (where a coupon could drop the order below the
        // threshold and silently add 19.90 of shipping).
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
        // The property Milestone 66 should have had and did not.
        //
        // Its allocation was validated by measuring that the shares always
        // sum back to the discount total - which they did, including when
        // one of them was negative. NodaMoney's Split emits a negative
        // share for roughly 1 in 200k weighted allocations, and a negative
        // discount share is a line whose discount *raises* its price.
        // Milestone 70 hit the same defect head-on in the refund
        // calculation and replaced Split on both paths; this is the check
        // that would have caught it the first time.
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
        // The consequence that actually reaches a customer: a line's share
        // of the discount must never exceed what that line costs, or its
        // net would be negative and the order would owe the shopper money
        // for buying it.
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
