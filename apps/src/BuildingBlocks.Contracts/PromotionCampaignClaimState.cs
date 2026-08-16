namespace BuildingBlocks;

/// <summary>
/// The states a campaign budget claim moves through - the budget-claim
/// mirror of CouponRedemptionState, for the same reason it lives here
/// rather than in Orders.Domain: Orders.Infrastructure claims the budget at
/// checkout, and Orders.Worker settles it when the saga finishes, and
/// Orders.Worker deliberately does not reference Orders.Domain.
/// </summary>
public static class PromotionCampaignClaimState
{
    /// <summary>Claimed by a checkout; counts against the campaign's budget.</summary>
    public const string Reserved = "Reserved";

    /// <summary>The order reached Confirmed - the claim is permanently spent.</summary>
    public const string Confirmed = "Confirmed";

    /// <summary>The order was cancelled - the claimed amount went back to the budget.</summary>
    public const string Released = "Released";
}
