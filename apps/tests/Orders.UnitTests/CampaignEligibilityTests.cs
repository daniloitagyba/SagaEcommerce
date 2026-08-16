using Orders.Domain;

namespace Orders.UnitTests;

/// <summary>
/// The budget-limited counterpart to CouponEligibilityTests (Milestone 90).
/// No per-customer limit, no NotFound case - CampaignEligibility.Evaluate
/// always receives an already-looked-up snapshot, since the caller resolved
/// it automatically rather than from a shopper-typed code.
/// </summary>
public class CampaignEligibilityTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 6, 12, 0, 0, TimeSpan.Zero);

    private static CampaignSnapshot Campaign(
        decimal discountAmount = 20m,
        decimal minimumOrderAmount = 0m,
        decimal budgetRemaining = 500m,
        int validFromDaysAgo = 1,
        int validUntilDaysAhead = 30,
        string? exclusivityGroup = null) =>
        new(
            "FLASH20",
            "R$20 off",
            discountAmount,
            Now.AddDays(-validFromDaysAgo),
            Now.AddDays(validUntilDaysAhead),
            minimumOrderAmount,
            exclusivityGroup,
            budgetRemaining);

    [Fact]
    public void AValidCampaignIsAccepted()
    {
        Assert.Equal(
            CampaignRejectionReason.None,
            CampaignEligibility.Evaluate(Campaign(), subtotal: 100m, Now));
    }

    [Fact]
    public void ACampaignIsRejectedBeforeItsWindowOpens()
    {
        var future = Campaign(validFromDaysAgo: -5);

        Assert.Equal(
            CampaignRejectionReason.NotYetValid,
            CampaignEligibility.Evaluate(future, subtotal: 100m, Now));
    }

    [Fact]
    public void ACampaignIsRejectedAfterItsWindowCloses()
    {
        var expired = Campaign(validUntilDaysAhead: -1);

        Assert.Equal(
            CampaignRejectionReason.Expired,
            CampaignEligibility.Evaluate(expired, subtotal: 100m, Now));
    }

    [Fact]
    public void TheValidityWindowIsHalfOpenSoTheExpiryInstantItselfIsAlreadyExpired()
    {
        var campaign = Campaign();
        var atExpiry = campaign.ValidUntil;

        Assert.Equal(
            CampaignRejectionReason.Expired,
            CampaignEligibility.Evaluate(campaign, subtotal: 100m, atExpiry));
        // ...and the instant it opens is already valid.
        Assert.Equal(
            CampaignRejectionReason.None,
            CampaignEligibility.Evaluate(campaign, subtotal: 100m, campaign.ValidFrom));
    }

    [Fact]
    public void AnOrderBelowTheMinimumIsRejected()
    {
        var campaign = Campaign(minimumOrderAmount: 200m);

        Assert.Equal(
            CampaignRejectionReason.BelowMinimumOrderAmount,
            CampaignEligibility.Evaluate(campaign, subtotal: 199.99m, Now));
        Assert.Equal(
            CampaignRejectionReason.None,
            CampaignEligibility.Evaluate(campaign, subtotal: 200m, Now));
    }

    [Fact]
    public void ACampaignWithNoBudgetLeftIsRejected()
    {
        var exhausted = Campaign(discountAmount: 20m, budgetRemaining: 19.99m);

        Assert.Equal(
            CampaignRejectionReason.BudgetExhausted,
            CampaignEligibility.Evaluate(exhausted, subtotal: 100m, Now));
    }

    [Fact]
    public void ACampaignWithExactlyEnoughBudgetForOneMoreClaimIsAccepted()
    {
        var campaign = Campaign(discountAmount: 20m, budgetRemaining: 20m);

        Assert.Equal(
            CampaignRejectionReason.None,
            CampaignEligibility.Evaluate(campaign, subtotal: 100m, Now));
    }
}
