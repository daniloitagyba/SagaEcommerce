using NodaMoney;
using NRules.Fluent.Dsl;
using NRules.RuleModel;
using Orders.Domain;
using Orders.Domain.Pricing;

namespace Orders.Application.Pricing;

/// <summary>
/// The policy facts the rules match against, inserted into the session
/// alongside the request. Carrying the policy as a fact (rather than
/// closing over an options object) is what lets a rule's condition depend
/// on configuration - "is this coupon code one we actually issue?" is a
/// data question, not a code question.
/// </summary>
public sealed record PromotionPolicy(PricingOptions Options);

/// <summary>
/// Marker fact: a rule decided this order ships free. The engine looks for
/// it instead of recomputing the threshold, so "free shipping" stays a
/// promotion decision that a future rule (loyalty tier, campaign week)
/// can also grant for entirely different reasons.
/// </summary>
public sealed record FreeShippingGranted(string Reason);

/// <summary>
/// A percentage-off coupon the shopper presented.
///
/// Milestone 67 moved coupons out of configuration and into the database,
/// where they have validity windows and redemption limits. By the time
/// this rule sees one it has already been looked up, checked and found
/// eligible (see CouponEligibility) - so the rule's only job is arithmetic,
/// and it stays a pure function of the facts in the session.
/// </summary>
public sealed class CouponPercentageRule : Rule
{
    public override void Define()
    {
        PricingRequest request = null!;

        // `!= null` rather than `is not null`: NRules compiles conditions
        // into expression trees, which cannot contain pattern matching.
        When()
            .Match(() => request, r => r.Coupon != null);

        Then()
            .Do(ctx => ctx.Insert(BuildDiscount(request)));
    }

    private static AppliedDiscount BuildDiscount(PricingRequest request)
    {
        var coupon = request.Coupon!;
        var amount = new Money(request.Subtotal.Amount * coupon.Percentage / 100m, request.Currency);
        return new AppliedDiscount(
            coupon.Code,
            $"{coupon.Percentage:0.##}% coupon {coupon.Code}",
            amount);
    }
}

/// <summary>
/// A category-wide promotion. Uses NRules' Collect aggregate to gather
/// every line in the discounted category - the kind of "all facts matching
/// a shape" query that reads as one declarative clause here and turns into
/// a nested group-by loop when written by hand.
/// </summary>
public sealed class CategoryDiscountRule : Rule
{
    public override void Define()
    {
        PricingRequest request = null!;
        PromotionPolicy policy = null!;
        IEnumerable<PricingLine> discountedLines = null!;

        When()
            .Match(() => policy)
            .Match(() => request)
            .Query(() => discountedLines, query => query
                .Match<PricingLine>()
                .Where(line => policy.Options.CategoryDiscounts.ContainsKey(line.CategorySlug))
                .Collect()
                .Where(lines => lines.Any()));

        Then()
            .Do(ctx => InsertPerCategory(ctx, request, policy, discountedLines));
    }

    private static void InsertPerCategory(
        IContext context,
        PricingRequest request,
        PromotionPolicy policy,
        IEnumerable<PricingLine> discountedLines)
    {
        var byCategory = discountedLines.GroupBy(line => line.CategorySlug, StringComparer.OrdinalIgnoreCase);

        foreach (var group in byCategory)
        {
            var percentage = policy.Options.CategoryDiscounts[group.Key];
            var categorySubtotal = group.Aggregate(
                new Money(0m, request.Currency),
                (running, line) => running + line.LineSubtotal);
            var amount = new Money(categorySubtotal.Amount * percentage / 100m, request.Currency);

            context.Insert(new AppliedDiscount(
                $"CATEGORY-{group.Key.ToUpperInvariant()}",
                $"{percentage:0.##}% off {group.Key}",
                amount));
        }
    }
}

/// <summary>
/// Volume pricing on a single SKU. Matches one line at a time, so the
/// engine evaluates it independently per line with no loop to get wrong.
/// </summary>
public sealed class BulkQuantityRule : Rule
{
    public override void Define()
    {
        PricingRequest request = null!;
        PromotionPolicy policy = null!;
        PricingLine line = null!;

        When()
            .Match(() => policy)
            .Match(() => request)
            .Match(() => line, candidate => candidate.Quantity >= policy.Options.BulkQuantityThreshold);

        Then()
            .Do(ctx => ctx.Insert(BuildDiscount(request, policy, line)));
    }

    private static AppliedDiscount BuildDiscount(PricingRequest request, PromotionPolicy policy, PricingLine line)
    {
        var percentage = policy.Options.BulkDiscountPercentage;
        var amount = new Money(line.LineSubtotal.Amount * percentage / 100m, request.Currency);
        return new AppliedDiscount(
            $"BULK-{line.Sku}",
            $"{percentage:0.##}% volume discount on {line.Quantity}x {line.Sku}",
            amount);
    }
}

/// <summary>
/// Milestone 71: a standing discount for customers who have earned it.
///
/// The fifth independently-authored rule, and the one that shows the
/// engine was worth having: it stacks with the coupon, the category
/// promotion and the volume discount without any of the four knowing the
/// others exist - and the cap the engine applies is what keeps the four of
/// them together from ever exceeding the order's value.
///
/// Unlike every other discount here, this one is not something the shopper
/// asks for. It applies because of who they are.
/// </summary>
public sealed class LoyaltyTierRule : Rule
{
    public override void Define()
    {
        PricingRequest request = null!;

        When()
            .Match(() => request, r => r.Customer != null && CustomerTiers.DiscountPercentageFor(r.Customer.Tier) > 0m);

        Then()
            .Do(ctx => ctx.Insert(BuildDiscount(request)));
    }

    private static AppliedDiscount BuildDiscount(PricingRequest request)
    {
        var tier = request.Customer!.Tier;
        var percentage = CustomerTiers.DiscountPercentageFor(tier);
        var amount = new Money(request.Subtotal.Amount * percentage / 100m, request.Currency);

        return new AppliedDiscount(
            $"TIER-{tier.ToUpperInvariant()}",
            $"{percentage:0.##}% {tier} member discount",
            amount);
    }
}

/// <summary>
/// Free shipping above a spend threshold - evaluated against the gross
/// subtotal, deliberately: a shopper who qualifies for free shipping and
/// then applies a coupon does not lose the free shipping, which is both
/// what storefronts do and what avoids a confusing order of operations.
/// </summary>
public sealed class FreeShippingRule : Rule
{
    public override void Define()
    {
        PricingRequest request = null!;
        PromotionPolicy policy = null!;

        When()
            .Match(() => policy)
            .Match(() => request, r => r.Subtotal.Amount >= policy.Options.FreeShippingThreshold);

        Then()
            .Do(ctx => ctx.Insert(new FreeShippingGranted(
                $"subtotal reached {policy.Options.FreeShippingThreshold:0.00}")));
    }
}
