using NodaMoney;
using NRules.Fluent.Dsl;
using NRules.RuleModel;
using Orders.Domain;
using Orders.Domain.Pricing;

namespace Orders.Application.Pricing;

/// <summary>
/// The policy facts the rules match against, inserted as a fact rather than
/// closed over so a rule's condition can depend on configuration.
/// </summary>
public sealed record PromotionPolicy(PricingOptions Options);

/// <summary>
/// Marker fact: a rule decided this order ships free, so other rules
/// (loyalty tier, campaign week) can grant it too without recomputing it.
/// </summary>
public sealed record FreeShippingGranted(string Reason);

/// <summary>
/// A percentage-off coupon the shopper presented, already looked up and
/// validated (see CouponEligibility) by the time this rule sees it - its
/// only job here is the arithmetic.
/// </summary>
public sealed class CouponPercentageRule : Rule
{
    public override void Define()
    {
        PricingRequest request = null!;

        // `!= null`, not `is not null`: NRules compiles conditions into expression trees.
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
/// A category-wide promotion, using NRules' Collect aggregate to gather
/// every line in the discounted category as one declarative clause.
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
/// Milestone 71: a standing discount for customers who have earned it -
/// not requested like the others, but stacked with them the same way, with
/// the engine's cap keeping all four from exceeding the order's value.
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
