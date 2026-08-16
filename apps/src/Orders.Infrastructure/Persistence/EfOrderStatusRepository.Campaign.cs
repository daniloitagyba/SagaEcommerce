using BuildingBlocks;
using Microsoft.EntityFrameworkCore;

namespace Orders.Infrastructure.Persistence;

public sealed partial class EfOrderStatusRepository
{
    /// <summary>
    /// The campaign-budget mirror of ReleaseCouponAsync - unconditional,
    /// same as its coupon counterpart: the guarded WHERE clause is what
    /// makes calling this for every cancellation safe even when no
    /// campaign was ever claimed for the order, rather than needing the
    /// caller to first check row.CampaignCode.
    /// </summary>
    private async Task ReleaseCampaignAsync(Guid orderId, CancellationToken cancellationToken)
    {
        await dbContext.Database.ExecuteSqlInterpolatedAsync(
            $"""
            WITH released AS (
                UPDATE promotion_campaign_claims
                SET state = {PromotionCampaignClaimState.Released}, settled_at = {DateTimeOffset.UtcNow}
                WHERE order_id = {orderId}
                  AND state IN ({PromotionCampaignClaimState.Reserved}, {PromotionCampaignClaimState.Confirmed})
                RETURNING code, amount
            )
            UPDATE promotion_campaigns
            SET budget_remaining = LEAST(budget_remaining + released.amount, total_budget)
            FROM released
            WHERE promotion_campaigns.code = released.code
            """,
            cancellationToken);
    }

    /// <summary>Mirrors ConfirmCouponAsync exactly - see its own comment for why this does not touch the budget column.</summary>
    private async Task ConfirmCampaignAsync(Guid orderId, CancellationToken cancellationToken)
    {
        await dbContext.Database.ExecuteSqlInterpolatedAsync(
            $"""
            UPDATE promotion_campaign_claims
            SET state = {PromotionCampaignClaimState.Confirmed}, settled_at = {DateTimeOffset.UtcNow}
            WHERE order_id = {orderId} AND state = {PromotionCampaignClaimState.Reserved}
            """,
            cancellationToken);
    }
}
