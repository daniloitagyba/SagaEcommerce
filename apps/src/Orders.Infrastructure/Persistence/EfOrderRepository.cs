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
    ResiliencePipelineProvider<string> pipelineProvider) : IOrderRepository
{
    private readonly ResiliencePipeline _pipeline = pipelineProvider.GetPipeline(ResilienceExtensions.PostgresPipeline);

    public async Task AddAsync(
        Order order,
        OutboxMessage outboxMessage,
        CouponReservation? couponReservation,
        CancellationToken cancellationToken)
    {
        // A lost redemption race is reported as a value, not thrown - the
        // Postgres pipeline retries every exception with no ShouldHandle
        // predicate, so throwing here would retry an exhausted coupon for
        // nothing and could trip the breaker for every other caller.
        string? redemptionFailure = null;

        try
        {
            await _pipeline.ExecuteAsync(async ct =>
            {
                await using var transaction = await dbContext.Database.BeginTransactionAsync(ct);

                if (couponReservation is not null)
                {
                    redemptionFailure = await TryReserveCouponAsync(couponReservation, ct);
                    if (redemptionFailure is not null)
                    {
                        await transaction.RollbackAsync(ct);
                        return;
                    }
                }

                dbContext.Orders.Add(order);
                dbContext.OutboxMessages.Add(outboxMessage);
                await dbContext.SaveChangesAsync(ct);
                await transaction.CommitAsync(ct);
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
    }

    /// <summary>
    /// Milestone 67: claims a redemption slot atomically. The guarded
    /// UPDATE closes the race - checking then incrementing would let N
    /// concurrent checkouts all read the same count and all pass a limit of
    /// 1 - same shape as OrderStatusStore's CAS and Inventory's reservation.
    /// The per-customer limit rides on that UPDATE's row lock: held until
    /// commit, so every competing redemption of the same coupon is blocked
    /// behind it by the time this transaction counts existing redemptions.
    /// </summary>
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
            // The caller rolls back, undoing the increment above.
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

    public async Task<Order?> FindByIdAsync(Guid id, CancellationToken cancellationToken)
    {
        try
        {
            // Milestone 66: Include is required, not an optimisation - AsNoTracking means EF won't lazily fill the lines in later.
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
}
