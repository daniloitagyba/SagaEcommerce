namespace Orders.Domain;

/// <summary>An automatic, budget-limited promotion resolved by pricing on every checkout, unlike a typed-in Coupon.</summary>
public sealed class PromotionCampaign
{
    private PromotionCampaign()
    {
    }

    public string Code { get; private set; } = string.Empty;

    public string Description { get; private set; } = string.Empty;

    /// <summary>A flat amount off, not a percentage, known upfront before the budget claim.</summary>
    public decimal DiscountAmount { get; private set; }

    public DateTimeOffset ValidFrom { get; private set; }

    public DateTimeOffset ValidUntil { get; private set; }

    public decimal MinimumOrderAmount { get; private set; }

    /// <summary>Campaigns sharing a group do not stack; the best-value one for a given order wins.</summary>
    public string? ExclusivityGroup { get; private set; }

    public decimal TotalBudget { get; private set; }

    /// <summary>Depletes by DiscountAmount on every claim, floored at 0.</summary>
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

/// <summary>One checkout's claim on a campaign's budget, reserved then confirmed or released as the order settles.</summary>
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

/// <summary>A campaign as checkout reads it: a point-in-time snapshot so eligibility cannot shift mid-decision.</summary>
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
    /// <summary>Pure, advisory eligibility check; the actual budget reservation still guards atomically.</summary>
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
