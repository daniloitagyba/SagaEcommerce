using BuildingBlocks;
using Microsoft.EntityFrameworkCore;
using Orders.Application.Exceptions;
using Orders.Application.Ports;
using Orders.Domain;
using Orders.Infrastructure.Data;
using Polly;
using Polly.Registry;

namespace Orders.Infrastructure.Persistence;

public sealed class EfOrderRepository(
    OrdersDbContext dbContext,
    ResiliencePipelineProvider<string> pipelineProvider) : IOrderCreationRepository
{
    private readonly ResiliencePipeline _pipeline = pipelineProvider.GetPipeline(ResilienceExtensions.PostgresPipeline);

    public async Task<OrderWriteResult> AddAsync(
        Order order,
        OutboxMessage outboxMessage,
        CouponReservation? couponReservation,
        OrderIdempotencyClaim? idempotencyClaim,
        CancellationToken cancellationToken,
        CampaignReservation? campaignReservation = null)
    {
        string? redemptionFailure = null;
        string? campaignFailure = null;
        OrderWriteResult? writeResult = null;

        try
        {
            await _pipeline.ExecuteAsync(async ct =>
            {
                redemptionFailure = null;
                campaignFailure = null;
                writeResult = null;
                await using var transaction = await dbContext.Database.BeginTransactionAsync(ct);

                if (await dbContext.Orders
                    .AsNoTracking()
                    .AnyAsync(item => item.Id == order.Id, ct))
                {
                    await transaction.RollbackAsync(ct);
                    writeResult = new OrderWriteResult(OrderWriteOutcome.Created, order.Id);
                    return;
                }

                if (idempotencyClaim is not null)
                {
                    var inserted = await dbContext.Database.ExecuteSqlInterpolatedAsync(
                        $"""
                        INSERT INTO order_idempotency (customer_id, idempotency_key, request_hash, order_id, created_at)
                        VALUES ({idempotencyClaim.CustomerId}, {idempotencyClaim.IdempotencyKey}, {idempotencyClaim.RequestHash}, {idempotencyClaim.OrderId}, {idempotencyClaim.CreatedAt})
                        ON CONFLICT (customer_id, idempotency_key) DO NOTHING
                        """,
                        ct);

                    if (inserted == 0)
                    {
                        var existing = await dbContext.OrderIdempotencyRecords
                            .AsNoTracking()
                            .SingleAsync(
                                item => item.CustomerId == idempotencyClaim.CustomerId
                                    && item.IdempotencyKey == idempotencyClaim.IdempotencyKey,
                                ct);
                        await transaction.RollbackAsync(ct);
                        var isReplay = string.Equals(existing.RequestHash, idempotencyClaim.RequestHash, StringComparison.Ordinal);
                        if (isReplay)
                        {
                            OrdersTelemetry.RecordIdempotentReplay();
                        }

                        writeResult = new OrderWriteResult(
                            isReplay ? OrderWriteOutcome.Replayed : OrderWriteOutcome.IdempotencyConflict,
                            existing.OrderId,
                            existing.RequestHash);
                        return;
                    }
                }

                if (couponReservation is not null)
                {
                    redemptionFailure = await TryReserveCouponAsync(couponReservation, ct);
                    if (redemptionFailure is not null)
                    {
                        await transaction.RollbackAsync(ct);
                        return;
                    }
                }

                if (campaignReservation is not null)
                {
                    campaignFailure = await TryReserveCampaignAsync(campaignReservation, ct);
                    if (campaignFailure is not null)
                    {
                        await transaction.RollbackAsync(ct);
                        return;
                    }
                }

                dbContext.Orders.Add(order);
                dbContext.OutboxMessages.Add(outboxMessage);
                await dbContext.SaveChangesAsync(ct);
                await transaction.CommitAsync(ct);
                writeResult = new OrderWriteResult(OrderWriteOutcome.Created, order.Id);
            }, cancellationToken);
        }
        catch (Exception exception) when (ResilienceExtensions.IsInfrastructureFault(exception))
        {
            throw new InfrastructureUnavailableException("PostgreSQL is currently unavailable.", exception);
        }

        if (redemptionFailure is not null)
        {
            throw new CouponRedemptionUnavailableException(couponReservation!.Code, redemptionFailure);
        }

        if (campaignFailure is not null)
        {
            throw new CampaignBudgetUnavailableException(campaignReservation!.Code, campaignFailure);
        }

        return writeResult
            ?? throw new InvalidOperationException("The order transaction completed without a persistence outcome.");
    }

    /// <summary>Atomically claims a coupon redemption slot via a guarded UPDATE, closing the check-then-increment race.</summary>
    /// <returns>Null when the slot was claimed; otherwise why it could not be.</returns>
    private async Task<string?> TryReserveCouponAsync(CouponReservation reservation, CancellationToken cancellationToken)
    {
        var claimed = await dbContext.Database.ExecuteSqlInterpolatedAsync(
            $"""
            UPDATE coupons
            SET redemption_count = redemption_count + 1
            WHERE code = {reservation.Code}
              AND valid_from <= {reservation.ReservedAt}
              AND valid_until > {reservation.ReservedAt}
              AND (max_total_redemptions IS NULL OR redemption_count < max_total_redemptions)
            """,
            cancellationToken);

        if (claimed == 0)
        {
            return "it is no longer valid or has reached its redemption limit";
        }

        var customerRedemptions = await dbContext.Database
            .SqlQuery<int>($"""
                SELECT COUNT(*)::int AS "Value"
                FROM coupon_redemptions
                WHERE code = {reservation.Code}
                  AND customer_id = {reservation.CustomerId}
                  AND state <> {CouponRedemptionState.Released}
                """)
            .SingleAsync(cancellationToken);

        var maxPerCustomer = await dbContext.Database
            .SqlQuery<int?>($"""SELECT max_per_customer AS "Value" FROM coupons WHERE code = {reservation.Code}""")
            .SingleAsync(cancellationToken);

        if (maxPerCustomer is { } limit && customerRedemptions >= limit)
        {
            return "this customer has already redeemed it the maximum number of times";
        }

        await dbContext.Database.ExecuteSqlInterpolatedAsync(
            $"""
            INSERT INTO coupon_redemptions (id, code, order_id, customer_id, state, reserved_at)
            VALUES ({Guid.NewGuid()}, {reservation.Code}, {reservation.OrderId}, {reservation.CustomerId}, {CouponRedemptionState.Reserved}, {reservation.ReservedAt})
            """,
            cancellationToken);

        return null;
    }

    /// <summary>Atomically claims a slice of a campaign's budget via the same guarded-UPDATE shape as TryReserveCouponAsync, with no per-customer limit.</summary>
    /// <returns>Null when the budget was claimed; otherwise why it could not be.</returns>
    private async Task<string?> TryReserveCampaignAsync(CampaignReservation reservation, CancellationToken cancellationToken)
    {
        var claimed = await dbContext.Database.ExecuteSqlInterpolatedAsync(
            $"""
            UPDATE promotion_campaigns
            SET budget_remaining = budget_remaining - {reservation.Amount}
            WHERE code = {reservation.Code}
              AND valid_from <= {reservation.ReservedAt}
              AND valid_until > {reservation.ReservedAt}
              AND budget_remaining >= {reservation.Amount}
            """,
            cancellationToken);

        if (claimed == 0)
        {
            return "it is no longer valid or its budget has been exhausted";
        }

        await dbContext.Database.ExecuteSqlInterpolatedAsync(
            $"""
            INSERT INTO promotion_campaign_claims (id, code, order_id, amount, state, reserved_at)
            VALUES ({Guid.NewGuid()}, {reservation.Code}, {reservation.OrderId}, {reservation.Amount}, {PromotionCampaignClaimState.Reserved}, {reservation.ReservedAt})
            """,
            cancellationToken);

        return null;
    }

    public async Task<Order?> FindByIdAsync(Guid id, CancellationToken cancellationToken)
    {
        try
        {
            return await _pipeline.ExecuteAsync(
                async ct => await dbContext.Orders
                    .AsNoTracking()
                    .Include(item => item.Lines)
                    .SingleOrDefaultAsync(item => item.Id == id, ct),
                cancellationToken);
        }
        catch (Exception exception) when (ResilienceExtensions.IsInfrastructureFault(exception))
        {
            throw new InfrastructureUnavailableException("PostgreSQL is currently unavailable.", exception);
        }
    }

    public async Task<OrderIdempotencyEntry?> FindIdempotencyAsync(
        string customerId,
        string idempotencyKey,
        CancellationToken cancellationToken)
    {
        try
        {
            return await _pipeline.ExecuteAsync(
                async ct => await dbContext.OrderIdempotencyRecords
                    .AsNoTracking()
                    .Where(item => item.CustomerId == customerId && item.IdempotencyKey == idempotencyKey)
                    .Select(item => new OrderIdempotencyEntry(item.OrderId, item.RequestHash))
                    .SingleOrDefaultAsync(ct),
                cancellationToken);
        }
        catch (Exception exception) when (ResilienceExtensions.IsInfrastructureFault(exception))
        {
            throw new InfrastructureUnavailableException("PostgreSQL is currently unavailable.", exception);
        }
    }
}
