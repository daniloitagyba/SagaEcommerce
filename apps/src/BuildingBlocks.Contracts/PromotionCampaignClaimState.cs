namespace BuildingBlocks;

/// <summary>The states a campaign budget claim moves through.</summary>
public static class PromotionCampaignClaimState
{
    /// <summary>Claimed by a checkout; counts against the campaign's budget.</summary>
    public const string Reserved = "Reserved";

    /// <summary>The order reached Confirmed - the claim is permanently spent.</summary>
    public const string Confirmed = "Confirmed";

    /// <summary>The order was cancelled - the claimed amount went back to the budget.</summary>
    public const string Released = "Released";
}
