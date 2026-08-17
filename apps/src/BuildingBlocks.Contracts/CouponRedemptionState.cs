namespace BuildingBlocks;

/// <summary>The states a coupon redemption moves through.</summary>
public static class CouponRedemptionState
{
    /// <summary>Claimed by a checkout; counts against the coupon's limits.</summary>
    public const string Reserved = "Reserved";

    /// <summary>The order reached Confirmed - the slot is permanently spent.</summary>
    public const string Confirmed = "Confirmed";

    /// <summary>The order was cancelled - the slot went back to the pool.</summary>
    public const string Released = "Released";
}
