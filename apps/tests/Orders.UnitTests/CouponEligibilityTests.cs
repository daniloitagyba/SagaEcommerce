using CsCheck;
using Orders.Domain;

namespace Orders.UnitTests;

/// <summary>The coupon rules a bare percentage mapping didn't have (expiry, redemption limits); CouponEligibility.Evaluate is a pure function, testable without a database.</summary>
public class CouponEligibilityTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 6, 12, 0, 0, TimeSpan.Zero);

    private static CouponSnapshot Coupon(
        decimal minimumOrderAmount = 0m,
        int? maxTotalRedemptions = null,
        int? maxPerCustomer = null,
        int redemptionCount = 0,
        int validFromDaysAgo = 1,
        int validUntilDaysAhead = 30) =>
        new(
            "SAVE10",
            "10% off",
            10m,
            Now.AddDays(-validFromDaysAgo),
            Now.AddDays(validUntilDaysAhead),
            minimumOrderAmount,
            maxTotalRedemptions,
            maxPerCustomer,
            redemptionCount);

    [Fact]
    public void AValidCouponIsAccepted()
    {
        Assert.Equal(
            CouponRejectionReason.None,
            CouponEligibility.Evaluate(Coupon(), subtotal: 100m, customerRedemptionCount: 0, Now));
    }

    [Fact]
    public void AnUnknownCodeIsRejectedRatherThanSilentlyIgnored()
    {
        Assert.Equal(
            CouponRejectionReason.NotFound,
            CouponEligibility.Evaluate(null, subtotal: 100m, customerRedemptionCount: 0, Now));
    }

    [Fact]
    public void ACouponIsRejectedBeforeItsWindowOpens()
    {
        var future = Coupon(validFromDaysAgo: -5);

        Assert.Equal(
            CouponRejectionReason.NotYetValid,
            CouponEligibility.Evaluate(future, subtotal: 100m, customerRedemptionCount: 0, Now));
    }

    [Fact]
    public void ACouponIsRejectedAfterItsWindowCloses()
    {
        var expired = Coupon(validUntilDaysAhead: -1);

        Assert.Equal(
            CouponRejectionReason.Expired,
            CouponEligibility.Evaluate(expired, subtotal: 100m, customerRedemptionCount: 0, Now));
    }

    [Fact]
    public void TheValidityWindowIsHalfOpenSoTheExpiryInstantItselfIsAlreadyExpired()
    {
        var coupon = Coupon();
        var atExpiry = coupon.ValidUntil;

        Assert.Equal(
            CouponRejectionReason.Expired,
            CouponEligibility.Evaluate(coupon, subtotal: 100m, customerRedemptionCount: 0, atExpiry));
        Assert.Equal(
            CouponRejectionReason.None,
            CouponEligibility.Evaluate(coupon, subtotal: 100m, customerRedemptionCount: 0, coupon.ValidFrom));
    }

    [Fact]
    public void AnOrderBelowTheMinimumIsRejected()
    {
        var coupon = Coupon(minimumOrderAmount: 200m);

        Assert.Equal(
            CouponRejectionReason.BelowMinimumOrderAmount,
            CouponEligibility.Evaluate(coupon, subtotal: 199.99m, customerRedemptionCount: 0, Now));
        Assert.Equal(
            CouponRejectionReason.None,
            CouponEligibility.Evaluate(coupon, subtotal: 200m, customerRedemptionCount: 0, Now));
    }

    [Fact]
    public void ACouponThatHasRunOutIsRejected()
    {
        var exhausted = Coupon(maxTotalRedemptions: 100, redemptionCount: 100);

        Assert.Equal(
            CouponRejectionReason.TotalRedemptionLimitReached,
            CouponEligibility.Evaluate(exhausted, subtotal: 100m, customerRedemptionCount: 0, Now));
    }

    [Fact]
    public void ACustomerWhoHasHitTheirOwnLimitIsRejectedEvenWhenTheCouponHasSlotsLeft()
    {
        var coupon = Coupon(maxTotalRedemptions: 100, redemptionCount: 3, maxPerCustomer: 1);

        Assert.Equal(
            CouponRejectionReason.CustomerRedemptionLimitReached,
            CouponEligibility.Evaluate(coupon, subtotal: 100m, customerRedemptionCount: 1, Now));
    }

    [Fact]
    public void NullLimitsMeanUnlimitedWhichIsTheOldBehaviourNowOptIn()
    {
        var unlimited = Coupon(maxTotalRedemptions: null, maxPerCustomer: null, redemptionCount: 1_000_000);

        Assert.Equal(
            CouponRejectionReason.None,
            CouponEligibility.Evaluate(unlimited, subtotal: 100m, customerRedemptionCount: 1_000_000, Now));
    }

    [Fact]
    public void ACouponWithALimitIsNeverAcceptedPastIt()
    {
        var gen =
            from maxTotal in Gen.Int[1, 50]
            from redeemed in Gen.Int[0, 80]
            from subtotalCents in Gen.Int[0, 100_000]
            from minimumCents in Gen.Int[0, 50_000]
            from customerRedemptions in Gen.Int[0, 5]
            from maxPerCustomer in Gen.Int[1, 5]
            select (maxTotal, redeemed, subtotalCents, minimumCents, customerRedemptions, maxPerCustomer);

        gen.Sample(
            input =>
            {
                var coupon = new CouponSnapshot(
                    "SAVE10", "10% off", 10m,
                    Now.AddDays(-1), Now.AddDays(30),
                    input.minimumCents / 100m,
                    input.maxTotal,
                    input.maxPerCustomer,
                    input.redeemed);

                var reason = CouponEligibility.Evaluate(
                    coupon, input.subtotalCents / 100m, input.customerRedemptions, Now);

                return reason != CouponRejectionReason.None
                    || (input.redeemed < input.maxTotal && input.customerRedemptions < input.maxPerCustomer);
            },
            iter: 10_000);
    }
}
