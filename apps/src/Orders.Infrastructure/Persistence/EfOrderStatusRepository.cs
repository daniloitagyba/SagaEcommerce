using System.Text.Json;
using BuildingBlocks;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Npgsql;
using NpgsqlTypes;
using Orders.Application.Exceptions;
using Orders.Application.Ports;
using Orders.Domain;
using Orders.Infrastructure.Data;
using Polly;
using Polly.Registry;

namespace Orders.Infrastructure.Persistence;

public sealed class EfOrderStatusRepository(
    OrdersDbContext dbContext,
    ResiliencePipelineProvider<string> pipelineProvider) : IOrderStatusRepository
{
    // Previous status alongside payment method - the read
    // needs a lock (FOR UPDATE), not just a plain SELECT, or a concurrent
    // transition landing between this read and the CAS-guarded UPDATE
    // below could make this repository believe the order arrived at
    // Cancelled from a different predecessor than it actually did, and
    // pick the wrong inventory compensation (restock vs. backorder-cancel)
    // for it. The UPDATE's own `previous.status = ANY(@allowed_from)`
    // guard still catches the ordinary lost-race case (someone else's
    // transition landing first); the lock closes the narrower gap where
    // two different but both-legal predecessors are in play at once.
    // coupon_code/customer_id/amount_cents ride along with the same
    // FOR UPDATE read as payment_method - Confirmed's own side effects
    // (coupon confirmation, loyalty tier) need them and a second read here
    // would just reopen the exact race the lock above already closes.
    private const string TransitionSql = """
        WITH previous AS (
            SELECT status, payment_method, coupon_code, customer_id, amount_cents FROM orders WHERE id = @id FOR UPDATE
        )
        UPDATE orders o
        SET status = @status
        FROM previous
        WHERE o.id = @id AND previous.status = ANY(@allowed_from)
        RETURNING previous.status, previous.payment_method, previous.coupon_code, previous.customer_id, previous.amount_cents
        """;

    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);
    private readonly ResiliencePipeline _pipeline = pipelineProvider.GetPipeline(ResilienceExtensions.PostgresPipeline);

    public async Task<OrderTransition> TryTransitionAsync(
        Guid orderId,
        string targetStatus,
        IReadOnlyList<string> allowedFrom,
        OrderSettlementAction settlementAction,
        string correlationId,
        CancellationToken cancellationToken)
    {
        try
        {
            return await _pipeline.ExecuteAsync(async ct =>
            {
                await using var transaction = await dbContext.Database.BeginTransactionAsync(ct);

                var row = await TryTransitionRowAsync(orderId, targetStatus, allowedFrom, ct);
                var previousStatus = row.PreviousStatus;
                var paymentMethod = row.PaymentMethod;

                if (previousStatus is null)
                {
                    // Distinguish "no such order" from "wrong state" - the caller turns them into 404 vs 409.
                    var exists = await dbContext.Orders.AnyAsync(item => item.Id == orderId, ct);
                    await transaction.RollbackAsync(ct);
                    return new OrderTransition(
                        exists ? OrderTransitionOutcome.NotApplicable : OrderTransitionOutcome.NotFound,
                        null);
                }

                // Same transaction as the status CAS above - the read-model
                // projection (Orders.Worker's OrderProjectionProcessor) only
                // ever learns an order's status from OrderCreated/PaymentDecided
                // otherwise, so a warehouse move or a shopper's self-service
                // cancellation through this repository never reached it,
                // leaving /orders/summary reporting a stale status forever
                // (not just until the next projection tick - no event meant
                // no update would ever arrive).
                var now = DateTimeOffset.UtcNow;
                dbContext.OutboxMessages.Add(OutboxMessage.Create(
                    Guid.NewGuid(),
                    nameof(OrderStatusChanged),
                    JsonSerializer.Serialize(
                        new OrderStatusChanged(Guid.NewGuid(), orderId, targetStatus, now, correlationId),
                        SerializerOptions),
                    now,
                    correlationId,
                    System.Diagnostics.Activity.Current?.Id,
                    System.Diagnostics.Activity.Current?.TraceStateString));

                // A cancellation must give the coupon slot
                // back, same as the saga-driven path - but this path can
                // release it in the same transaction, since the coupon
                // lives in the same database as the order.
                if (targetStatus == OrderStatuses.Cancelled)
                {
                    await ReleaseCouponAsync(orderId, ct);
                }

                // Confirmed's own two side effects - previously only
                // Orders.Worker's OrderStatusStore applied these (the
                // saga-driven path), so an operator confirming an order
                // through this endpoint (Confirmed is a legal
                // FulfillmentEndpoints target - see OrderStatuses.PredecessorsOf)
                // left the coupon parked at Reserved forever and never grew
                // the customer's loyalty standing at all. Guarded on the
                // *previous* row's coupon_code/customer_id - the same facts
                // OrderStatusStore.ApplySideEffectsAsync reads off its own
                // RETURNING clause - so this fires exactly once, from the
                // one call that actually won the CAS above.
                if (targetStatus == OrderStatuses.Confirmed)
                {
                    if (row.CouponCode is not null)
                    {
                        await ConfirmCouponAsync(orderId, ct);
                    }

                    if (row.CustomerId is not null)
                    {
                        await RecordCompletedOrderForTierAsync(row.CustomerId, row.Amount, ct);
                    }
                }

                // The mirror of Confirmed's tier recording above: a
                // cancellation reaching here from Confirmed, Picking or
                // FulfillmentHold means the order was Confirmed at some
                // earlier point (the state graph guarantees it - those are
                // the only three of Cancelled's legal predecessors that are
                // reachable only through Confirmed; Created and Backordered
                // are not) and RecordCompletedOrderForTierAsync already ran
                // for it, crediting lifetime_spend permanently even though
                // the order never completed. Without this, confirm-then-
                // cancel was the cheapest route to a loyalty tier
                // CustomerTierStore's own header describes recording-at-
                // confirmation as having closed.
                if (targetStatus == OrderStatuses.Cancelled
                    && row.CustomerId is not null
                    && previousStatus is OrderStatuses.Confirmed or OrderStatuses.Picking or OrderStatuses.FulfillmentHold)
                {
                    await ReverseCompletedOrderForTierAsync(row.CustomerId, row.Amount, ct);
                }

                // Same transaction as the status change - a capture command outliving a rolled-back "Shipped" would charge for goods that never left.
                if (settlementAction == OrderSettlementAction.Capture && paymentMethod is not null && PaymentMethods.RequiresCapture(paymentMethod))
                {
                    QueueSettlementCommand(orderId, settlementAction, targetStatus, correlationId);
                }
                else if (settlementAction == OrderSettlementAction.Cancel)
                {
                    // Unlike capture, cancellation is not
                    // method-gated - a Pix payment is Captured the moment
                    // it's approved, so cancelling it has to refund, not
                    // void a hold that was never placed. Payments decides
                    // which of the two from the payment's own state
                    // (Payment.TryCancel); this path always queues the
                    // command, unconditionally.
                    QueueSettlementCommand(orderId, settlementAction, targetStatus, correlationId);
                    await QueueInventoryCompensationAsync(orderId, previousStatus, correlationId, ct);
                    await FlagInFlightSagaAsCancelledAsync(orderId, previousStatus, ct);
                }

                // Unconditional (not just inside the branches above) - the
                // OrderStatusChanged outbox row above still needs flushing
                // even when settlementAction is None (e.g. Picking,
                // Delivered, Confirmed-from-Backordered), which neither
                // branch above touches at all.
                await dbContext.SaveChangesAsync(ct);
                await transaction.CommitAsync(ct);
                return new OrderTransition(OrderTransitionOutcome.Advanced, paymentMethod);
            }, cancellationToken);
        }
        catch (Exception exception) when (ResilienceExtensions.IsInfrastructureFault(exception))
        {
            throw new InfrastructureUnavailableException("PostgreSQL is currently unavailable.", exception);
        }
    }

    /// <summary>
    /// The CAS itself, as one atomic statement (lock, guard, write, and
    /// return the row's state <em>before</em> the write, all in a single
    /// round trip) - see <see cref="TransitionSql"/>. Raw ADO.NET, not
    /// EF's <c>ExecuteSqlInterpolatedAsync</c>, because that helper only
    /// ever returns an affected-row count and this needs the RETURNING
    /// columns back; the same reason Orders.Worker's OrderStatusStore
    /// already talks to Npgsql directly for its own CAS.
    /// </summary>
    private async Task<TransitionRow> TryTransitionRowAsync(
        Guid orderId,
        string targetStatus,
        IReadOnlyList<string> allowedFrom,
        CancellationToken cancellationToken)
    {
        var connection = (NpgsqlConnection)dbContext.Database.GetDbConnection();
        var dbTransaction = (NpgsqlTransaction)dbContext.Database.CurrentTransaction!.GetDbTransaction();

        await using var command = connection.CreateCommand();
        command.Transaction = dbTransaction;
        command.CommandText = TransitionSql;
        command.Parameters.AddWithValue("id", NpgsqlDbType.Uuid, orderId);
        command.Parameters.AddWithValue("status", NpgsqlDbType.Varchar, targetStatus);
        command.Parameters.AddWithValue("allowed_from", NpgsqlDbType.Array | NpgsqlDbType.Varchar, allowedFrom.ToArray());

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return TransitionRow.NotTransitioned;
        }

        return new TransitionRow(
            reader.GetString(0),
            await reader.IsDBNullAsync(1, cancellationToken) ? null : reader.GetString(1),
            await reader.IsDBNullAsync(2, cancellationToken) ? null : reader.GetString(2),
            await reader.IsDBNullAsync(3, cancellationToken) ? null : reader.GetString(3),
            reader.GetInt64(4) / 100m);
    }

    private sealed record TransitionRow(
        string? PreviousStatus,
        string? PaymentMethod,
        string? CouponCode,
        string? CustomerId,
        decimal Amount)
    {
        public static readonly TransitionRow NotTransitioned = new(null, null, null, null, 0m);
    }

    /// <summary>
    /// The inventory side of cancelling, chosen from where
    /// the order actually was, not guessed:
    ///
    /// <list type="bullet">
    /// <item>Confirmed/Picking/FulfillmentHold: the reservation was
    /// already committed (drawn permanently out of stock) - the units come
    /// back via the same restock command a return uses.</item>
    /// <item>Backordered: nothing was ever reserved, only a place in the
    /// FIFO queue - that place is what needs to be given up.</item>
    /// <item>Created: nothing is queued from here either - not because the
    /// saga is invisible (see <see cref="FlagInFlightSagaAsCancelledAsync"/>,
    /// which runs alongside this method and is not invisible to it at all,
    /// both being raw SQL against the one Postgres database Orders.Api and
    /// Orders.Worker already share), but because at the moment this method
    /// runs, what if anything was reserved is still unknown - the saga may
    /// not have started, may have partially reserved, or may already have
    /// committed. Guessing here would still risk conjuring or destroying
    /// stock; flagging the row instead lets the saga's own reply consumer
    /// react with the one thing that always knows the truth - the reply
    /// that is actually, currently, in flight.</item>
    /// </list>
    /// </summary>
    private async Task QueueInventoryCompensationAsync(
        Guid orderId,
        string previousStatus,
        string correlationId,
        CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;

        if (previousStatus == OrderStatuses.Backordered)
        {
            var request = new BackorderCancellationRequested(orderId, correlationId, now);
            dbContext.OutboxMessages.Add(OutboxMessage.Create(
                Guid.NewGuid(),
                nameof(BackorderCancellationRequested),
                JsonSerializer.Serialize(request, SerializerOptions),
                now,
                correlationId,
                System.Diagnostics.Activity.Current?.Id,
                System.Diagnostics.Activity.Current?.TraceStateString));
            return;
        }

        if (previousStatus is not (OrderStatuses.Confirmed or OrderStatuses.Picking or OrderStatuses.FulfillmentHold))
        {
            return;
        }

        var lines = await dbContext.OrderLines
            .Where(line => line.OrderId == orderId)
            .Select(line => new { line.Sku, line.Quantity })
            .ToListAsync(cancellationToken);

        foreach (var line in lines)
        {
            // A fresh id per line, not the order id shared across all of
            // them - Inventory's restock inbox is deduplicated on this id
            // (see InventoryReservationMessageProcessor.ProcessSettlementAsync),
            // and sharing one across several SKUs would make every line
            // past the first look like a redelivered duplicate of it and
            // get silently skipped, never restocked. The same bug existed
            // in EfOrderReturnRepository.QueueRestockCommands for a
            // multi-SKU return before; fixed there too.
            var request = new InventoryRestockRequested(Guid.NewGuid(), orderId, line.Sku, line.Quantity, correlationId, now);

            dbContext.OutboxMessages.Add(OutboxMessage.Create(
                Guid.NewGuid(),
                nameof(InventoryRestockRequested),
                JsonSerializer.Serialize(request, SerializerOptions),
                now,
                correlationId,
                System.Diagnostics.Activity.Current?.Id,
                System.Diagnostics.Activity.Current?.TraceStateString));
        }
    }

    /// <summary>
    /// Closes the race a cancellation from Created or Backordered has
    /// always had: this order might still have an orchestrated saga row
    /// in flight (<c>saga_orchestration_states</c>), reserving or
    /// committing inventory this repository has no way to know the
    /// current status of. Rather than guess, it marks the row - a plain,
    /// idempotent UPDATE that no-ops if no such row exists (the ordinary
    /// case), or if the order isn't reachable through the choreographed
    /// saga at all. OrderSagaReplyConsumer checks this flag at the two
    /// points where it would otherwise commit the order to keep something
    /// this cancellation just gave up: the moment every line finishes
    /// reserving (it releases them instead of requesting a payment
    /// decision), and the moment every line's commit reply arrives (it
    /// restocks whatever was actually committed instead of confirming the
    /// order). Same table, same database, same transaction as the status
    /// CAS above - not a second write that could land only one of the two.
    /// </summary>
    private async Task FlagInFlightSagaAsCancelledAsync(Guid orderId, string previousStatus, CancellationToken cancellationToken)
    {
        if (previousStatus is not (OrderStatuses.Created or OrderStatuses.Backordered))
        {
            return;
        }

        await dbContext.Database.ExecuteSqlInterpolatedAsync(
            $"""
            UPDATE saga_orchestration_states
            SET cancellation_requested_at = COALESCE(cancellation_requested_at, {DateTimeOffset.UtcNow})
            WHERE order_id = {orderId}
            """,
            cancellationToken);
    }

    /// <summary>
    /// Same guard as Orders.Worker's CouponRedemptionStore: releasable from
    /// Reserved or Confirmed, never from Released, so a slot is handed back
    /// exactly once no matter how many times a cancellation is retried.
    /// </summary>
    private async Task ReleaseCouponAsync(Guid orderId, CancellationToken cancellationToken)
    {
        await dbContext.Database.ExecuteSqlInterpolatedAsync(
            $"""
            WITH released AS (
                UPDATE coupon_redemptions
                SET state = {CouponRedemptionState.Released}, settled_at = {DateTimeOffset.UtcNow}
                WHERE order_id = {orderId}
                  AND state IN ({CouponRedemptionState.Reserved}, {CouponRedemptionState.Confirmed})
                RETURNING code
            )
            UPDATE coupons
            SET redemption_count = GREATEST(redemption_count - 1, 0)
            FROM released
            WHERE coupons.code = released.code
            """,
            cancellationToken);
    }

    /// <summary>
    /// Mirrors Orders.Worker's CouponRedemptionStore.ConfirmSql exactly -
    /// guarded on Reserved so a redelivered/second-path confirm is a no-op,
    /// not a double count. Does not touch coupons.redemption_count: that
    /// column tracks reservations, already incremented at checkout, and
    /// confirming a redemption doesn't reserve a second one.
    /// </summary>
    private async Task ConfirmCouponAsync(Guid orderId, CancellationToken cancellationToken)
    {
        await dbContext.Database.ExecuteSqlInterpolatedAsync(
            $"""
            UPDATE coupon_redemptions
            SET state = {CouponRedemptionState.Confirmed}, settled_at = {DateTimeOffset.UtcNow}
            WHERE order_id = {orderId} AND state = {CouponRedemptionState.Reserved}
            """,
            cancellationToken);
    }

    /// <summary>
    /// Mirrors Orders.Worker's CustomerTierStore.RecordSql exactly,
    /// including using one atomic UPDATE to increment and re-derive tier
    /// together - two orders for the same customer confirming through
    /// different paths (this one and the saga's own) at the same moment
    /// must not each compute a new tier from a lifetime_spend that doesn't
    /// yet include the other's contribution. Orders.Domain.CustomerTiers is
    /// the threshold source of truth here, unlike CustomerTierStore's own
    /// copy - Orders.Worker deliberately doesn't reference Orders.Domain,
    /// but Orders.Infrastructure already does.
    /// </summary>
    private async Task RecordCompletedOrderForTierAsync(string customerId, decimal amount, CancellationToken cancellationToken)
    {
        await dbContext.Database.ExecuteSqlInterpolatedAsync(
            $"""
            UPDATE customers
            SET lifetime_spend = lifetime_spend + {amount},
                completed_order_count = completed_order_count + 1,
                tier = CASE
                    WHEN lifetime_spend + {amount} >= {CustomerTiers.GoldThreshold} THEN {CustomerTiers.Gold}
                    WHEN lifetime_spend + {amount} >= {CustomerTiers.SilverThreshold} THEN {CustomerTiers.Silver}
                    ELSE {CustomerTiers.Bronze}
                END
            WHERE id = {customerId}
            """,
            cancellationToken);
    }

    /// <summary>
    /// Mirrors Orders.Worker's CustomerTierStore.ReverseSql exactly,
    /// including deliberately not re-deriving tier downward - see that
    /// method's own doc comment for why. Symmetric with
    /// RecordCompletedOrderForTierAsync's GREATEST(...,0) counterpart in
    /// CouponRedemptionStore.ReleaseSql, floored the same way so a
    /// cancellation can never push either column negative.
    /// </summary>
    private async Task ReverseCompletedOrderForTierAsync(string customerId, decimal amount, CancellationToken cancellationToken)
    {
        await dbContext.Database.ExecuteSqlInterpolatedAsync(
            $"""
            UPDATE customers
            SET lifetime_spend = GREATEST(lifetime_spend - {amount}, 0),
                completed_order_count = GREATEST(completed_order_count - 1, 0)
            WHERE id = {customerId}
            """,
            cancellationToken);
    }

    private void QueueSettlementCommand(
        Guid orderId,
        OrderSettlementAction action,
        string targetStatus,
        string correlationId)
    {
        var now = DateTimeOffset.UtcNow;

        var (eventType, payload) = action == OrderSettlementAction.Capture
            ? (nameof(PaymentCaptureRequested),
               JsonSerializer.Serialize(new PaymentCaptureRequested(orderId, correlationId, now), SerializerOptions))
            : (nameof(PaymentCancellationRequested),
               JsonSerializer.Serialize(new PaymentCancellationRequested(orderId, $"order {targetStatus.ToLowerInvariant()}", correlationId, now), SerializerOptions));

        dbContext.OutboxMessages.Add(OutboxMessage.Create(
            Guid.NewGuid(),
            eventType,
            payload,
            now,
            correlationId,
            System.Diagnostics.Activity.Current?.Id,
            System.Diagnostics.Activity.Current?.TraceStateString));
    }
}
