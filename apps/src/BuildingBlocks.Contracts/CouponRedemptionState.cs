namespace BuildingBlocks;

/// <summary>
/// Milestone 67: the states a coupon redemption moves through.
///
/// Lives here rather than in Orders.Domain because both sides of the
/// redemption's life need it and they sit in different assemblies:
/// Orders.Infrastructure reserves the slot at checkout, and Orders.Worker
/// settles it when the saga finishes - and Orders.Worker deliberately does
/// not reference Orders.Domain (it is a separate service that talks to the
/// same database through raw SQL, see OrderStatusStore). Bare strings in
/// two places would drift; this is the same reasoning that already put
/// SagaStep and the messaging contracts in BuildingBlocks.Contracts.
/// </summary>
public static class CouponRedemptionState
{
    /// <summary>Claimed by a checkout; counts against the coupon's limits.</summary>
    public const string Reserved = "Reserved";

    /// <summary>The order reached Confirmed - the slot is permanently spent.</summary>
    public const string Confirmed = "Confirmed";

    /// <summary>The order was cancelled - the slot went back to the pool.</summary>
    public const string Released = "Released";
}
