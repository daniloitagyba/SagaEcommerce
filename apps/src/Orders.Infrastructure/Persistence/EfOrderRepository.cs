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
        // A lost redemption race is reported back out of the pipeline as a
        // value, not thrown through it. The Postgres pipeline's retry has no
        // ShouldHandle predicate, so it retries every exception and feeds
        // the circuit breaker with each one - throwing here would mean an
        // exhausted coupon got retried twice for nothing and, in a burst,
        // could trip the breaker for every other Postgres caller. The
        // failure is real but it is a business outcome, not a fault.
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
    /// Milestone 67: claims a redemption slot atomically.
    ///
    /// The guarded UPDATE is what actually closes the race - checking the
    /// count and then incrementing it would let N concurrent checkouts all
    /// read the same count and all pass a limit of 1. Postgres evaluates
    /// the WHERE and applies the increment as one operation, so exactly one
    /// of them affects a row and the rest see zero. Identical in shape to
    /// OrderStatusStore's status CAS and to Inventory's stock reservation:
    /// the guard lives in the WHERE clause, never in application code.
    ///
    /// The per-customer limit rides on a side effect of that same
    /// statement: an UPDATE takes a row lock on the coupon held until
    /// commit, so by the time this transaction counts that customer's
    /// existing redemptions, every competing redemption of the same coupon
    /// is blocked behind it. No second lock statement, and contention stays
    /// scoped to one coupon - the same way inventory contention is scoped
    /// to one SKU rather than the whole catalogue.
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
            // The caller rolls back, undoing the increment above - the slot
            // is never left claimed by a checkout that turned out to be
            // ineligible.
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
            // Milestone 66: Include is required, not an optimisation - the
            // order's pricing breakdown is unreadable without its lines,
            // and AsNoTracking means EF will not lazily fill them in later.
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
