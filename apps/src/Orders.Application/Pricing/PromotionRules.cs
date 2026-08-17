using NodaMoney;
using NRules.Fluent.Dsl;
using NRules.RuleModel;
using Orders.Domain;
using Orders.Domain.Pricing;

namespace Orders.Application.Pricing;

/// <summary>
/// Provides promotion policy facts.
/// </summary>
public sealed record PromotionPolicy(PricingOptions Options);

/// <summary>
/// Marks an order as eligible for free shipping.
/// </summary>
public sealed record FreeShippingGranted(string Reason);

/// <summary>
/// Applies a percentage coupon discount.
/// </summary>
public sealed class CouponPercentageRule : Rule
{
    public override void Define()
    {
        PricingRequest request = null!;

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
            amount,
            coupon.ExclusivityGroup);
    }
}

/// <summary>
/// Applies a promotion campaign discount.
/// </summary>
public sealed class CampaignDiscountRule : Rule
{
    public override void Define()
    {
        PricingRequest request = null!;

        When()
            .Match(() => request, r => r.Campaign != null);

        Then()
            .Do(ctx => ctx.Insert(BuildDiscount(request)));
    }

    private static AppliedDiscount BuildDiscount(PricingRequest request)
    {
        var campaign = request.Campaign!;
        var amount = campaign.Amount > request.Subtotal.Amount
            ? request.Subtotal
            : new Money(campaign.Amount, request.Currency);
        return new AppliedDiscount(campaign.Code, campaign.Description, amount, campaign.ExclusivityGroup);
    }
}

/// <summary>
/// Applies a category discount.
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
            .Match(() => request, r => policy.Options.CategoryDiscountWindow == null || policy.Options.CategoryDiscountWindow.IsActive(r.EffectiveEvaluatedAt))
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
        var exclusivityGroup = policy.Options.CategoryDiscountWindow?.ExclusivityGroup;

        foreach (var categoryGroup in byCategory)
        {
            var percentage = policy.Options.CategoryDiscounts[categoryGroup.Key];
            var categorySubtotal = categoryGroup.Aggregate(
                new Money(0m, request.Currency),
                (running, line) => running + line.LineSubtotal);
            var amount = new Money(categorySubtotal.Amount * percentage / 100m, request.Currency);

            context.Insert(new AppliedDiscount(
                $"CATEGORY-{categoryGroup.Key.ToUpperInvariant()}",
                $"{percentage:0.##}% off {categoryGroup.Key}",
                amount,
                exclusivityGroup));
        }
    }
}

/// <summary>
/// Volume pricing on a single SKU, matched and evaluated independently per line.
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
            .Match(() => request, r => policy.Options.BulkDiscountWindow == null || policy.Options.BulkDiscountWindow.IsActive(r.EffectiveEvaluatedAt))
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
            amount,
            policy.Options.BulkDiscountWindow?.ExclusivityGroup);
    }
}

/// <summary>
/// Applies a customer tier discount.
/// </summary>
public sealed class LoyaltyTierRule : Rule
{
    public override void Define()
    {
        PricingRequest request = null!;
        PromotionPolicy policy = null!;

        When()
            .Match(() => policy)
            .Match(() => request, r =>
                r.Customer != null && CustomerTiers.DiscountPercentageFor(r.Customer.Tier) > 0m
                && (policy.Options.TierDiscountWindow == null || policy.Options.TierDiscountWindow.IsActive(r.EffectiveEvaluatedAt)));

        Then()
            .Do(ctx => ctx.Insert(BuildDiscount(request, policy)));
    }

    private static AppliedDiscount BuildDiscount(PricingRequest request, PromotionPolicy policy)
    {
        var tier = request.Customer!.Tier;
        var percentage = CustomerTiers.DiscountPercentageFor(tier);
        var amount = new Money(request.Subtotal.Amount * percentage / 100m, request.Currency);

        return new AppliedDiscount(
            $"TIER-{tier.ToUpperInvariant()}",
            $"{percentage:0.##}% {tier} member discount",
            amount,
            policy.Options.TierDiscountWindow?.ExclusivityGroup);
    }
}

/// <summary>
/// Applies free shipping above a threshold.
/// </summary>
public sealed class FreeShippingRule : Rule
{
    public override void Define()
    {
        PricingRequest request = null!;
        PromotionPolicy policy = null!;

        When()
            .Match(() => policy)
            .Match(() => request, r =>
                r.Subtotal.Amount >= policy.Options.FreeShippingThreshold
                && (policy.Options.FreeShippingWindow == null || policy.Options.FreeShippingWindow.IsActive(r.EffectiveEvaluatedAt)));

        Then()
            .Do(ctx => ctx.Insert(new FreeShippingGranted(
                $"subtotal reached {policy.Options.FreeShippingThreshold:0.00}")));
    }
}
