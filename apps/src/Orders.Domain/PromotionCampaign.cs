namespace Orders.Domain;

/// <summary>
/// An automatic, budget-limited promotion - the "campaign budget" leg of
/// Milestone 90 (docs/roadmap-milestones-81-90.md, Phase 5). Unlike a
/// Coupon, nobody types a code for this: OrderPricingService resolves the
/// best currently-active, still-funded campaign on every checkout, the same
/// way it resolves customer tier. The budget is money, not a count, so it
/// gets the same atomic-guarded-UPDATE treatment Coupon's
/// MaxTotalRedemptions already uses (see EfOrderRepository.TryReserveCampaignAsync),
/// just decrementing a decimal instead of an int.
/// </summary>
public sealed class PromotionCampaign
{
    private PromotionCampaign()
    {
    }

    public string Code { get; private set; } = string.Empty;

    public string Description { get; private set; } = string.Empty;

    /// <summary>A flat amount off, not a percentage - known upfront, so the budget claim never has to wait on pricing to know what it's claiming.</summary>
    public decimal DiscountAmount { get; private set; }

    public DateTimeOffset ValidFrom { get; private set; }

    public DateTimeOffset ValidUntil { get; private set; }

    public decimal MinimumOrderAmount { get; private set; }

    /// <summary>Campaigns sharing a group do not stack - the best-value one for a given order wins; see NRulesPricingEngine's exclusivity reduction.</summary>
    public string? ExclusivityGroup { get; private set; }

    public decimal TotalBudget { get; private set; }

    /// <summary>Depletes by DiscountAmount on every claim, floored at 0 - never negative, the same GREATEST(...,0) discipline every other counter in this codebase uses on release.</summary>
    public decimal BudgetRemaining { get; private set; }

    public static PromotionCampaign Create(
        string code,
        string description,
        decimal discountAmount,
        DateTimeOffset validFrom,
        DateTimeOffset validUntil,
        decimal minimumOrderAmount,
        decimal totalBudget,
        string? exclusivityGroup = null)
    {
        return new PromotionCampaign
        {
            Code = code.Trim().ToUpperInvariant(),
            Description = description,
            DiscountAmount = discountAmount,
            ValidFrom = validFrom,
            ValidUntil = validUntil,
            MinimumOrderAmount = minimumOrderAmount,
            ExclusivityGroup = exclusivityGroup,
            TotalBudget = totalBudget,
            BudgetRemaining = totalBudget
        };
    }
}

/// <summary>
/// One checkout's claim on a campaign's budget - reserved, then confirmed
/// or released as the order settles, exactly mirroring CouponRedemption's
/// lifecycle and for the same reason: the claim participates in the saga,
/// so a cancelled order gives its share of the budget back.
/// </summary>
public sealed class PromotionCampaignClaim
{
    private PromotionCampaignClaim()
    {
    }

    public Guid Id { get; private set; }

    public string Code { get; private set; } = string.Empty;

    /// <summary>Unique: an order claims a given campaign at most once.</summary>
    public Guid OrderId { get; private set; }

    public decimal Amount { get; private set; }

    public string State { get; private set; } = string.Empty;

    public DateTimeOffset ReservedAt { get; private set; }

    public DateTimeOffset? SettledAt { get; private set; }
}

/// <summary>Why a campaign could not be applied - mirrors CouponRejectionReason, minus the per-customer limit a campaign has no concept of.</summary>
public enum CampaignRejectionReason
{
    None,
    NotYetValid,
    Expired,
    BelowMinimumOrderAmount,
    BudgetExhausted
}

/// <summary>
/// A campaign as the checkout reads it - a point-in-time snapshot, the same
/// reasoning as CouponSnapshot: eligibility has to be decided from values
/// that cannot shift underneath the decision.
/// </summary>
public sealed record CampaignSnapshot(
    string Code,
    string Description,
    decimal DiscountAmount,
    DateTimeOffset ValidFrom,
    DateTimeOffset ValidUntil,
    decimal MinimumOrderAmount,
    string? ExclusivityGroup,
    decimal BudgetRemaining);

public static class CampaignEligibility
{
    /// <summary>
    /// Pure and advisory, same caveat as CouponEligibility.Evaluate: the
    /// budget it reads can still be taken by a concurrent checkout before
    /// the real reservation closes that race with an atomic guarded UPDATE.
    /// </summary>
    public static CampaignRejectionReason Evaluate(CampaignSnapshot campaign, decimal subtotal, DateTimeOffset now)
    {
        if (now < campaign.ValidFrom)
        {
            return CampaignRejectionReason.NotYetValid;
        }

        if (now >= campaign.ValidUntil)
        {
            return CampaignRejectionReason.Expired;
        }

        if (subtotal < campaign.MinimumOrderAmount)
        {
            return CampaignRejectionReason.BelowMinimumOrderAmount;
        }

        if (campaign.BudgetRemaining < campaign.DiscountAmount)
        {
            return CampaignRejectionReason.BudgetExhausted;
        }

        return CampaignRejectionReason.None;
    }
}
