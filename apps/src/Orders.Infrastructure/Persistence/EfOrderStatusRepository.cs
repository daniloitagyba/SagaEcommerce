using System.Text.Json;
using BuildingBlocks;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Npgsql;
using NpgsqlTypes;
using Orders.Application.Exceptions;
using Orders.Application.Ports;
using Orders.Infrastructure.Data;
using Polly;
using Polly.Registry;

namespace Orders.Infrastructure.Persistence;

public sealed class EfOrderStatusRepository(
    OrdersDbContext dbContext,
    ResiliencePipelineProvider<string> pipelineProvider) : IOrderStatusRepository
{
    // Milestone 81: previous status alongside payment method - the read
    // needs a lock (FOR UPDATE), not just a plain SELECT, or a concurrent
    // transition landing between this read and the CAS-guarded UPDATE
    // below could make this repository believe the order arrived at
    // Cancelled from a different predecessor than it actually did, and
    // pick the wrong inventory compensation (restock vs. backorder-cancel)
    // for it. The UPDATE's own `previous.status = ANY(@allowed_from)`
    // guard still catches the ordinary lost-race case (someone else's
    // transition landing first); the lock closes the narrower gap where
    // two different but both-legal predecessors are in play at once.
    private const string TransitionSql = """
        WITH previous AS (
            SELECT status, payment_method FROM orders WHERE id = @id FOR UPDATE
        )
        UPDATE orders o
        SET status = @status
        FROM previous
        WHERE o.id = @id AND previous.status = ANY(@allowed_from)
        RETURNING previous.status, previous.payment_method
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

                var (previousStatus, paymentMethod) = await TryTransitionRowAsync(orderId, targetStatus, allowedFrom, ct);

                if (previousStatus is null)
                {
                    // Distinguish "no such order" from "wrong state" - the caller turns them into 404 vs 409.
                    var exists = await dbContext.Orders.AnyAsync(item => item.Id == orderId, ct);
                    await transaction.RollbackAsync(ct);
                    return new OrderTransition(
                        exists ? OrderTransitionOutcome.NotApplicable : OrderTransitionOutcome.NotFound,
                        null);
                }

                // Milestone 69: a cancellation must give the coupon slot
                // back, same as the saga-driven path - but this path can
                // release it in the same transaction, since the coupon
                // lives in the same database as the order.
                if (targetStatus == OrderStatuses.Cancelled)
                {
                    await ReleaseCouponAsync(orderId, ct);
                }

                // Same transaction as the status change - a capture command outliving a rolled-back "Shipped" would charge for goods that never left.
                if (settlementAction == OrderSettlementAction.Capture && paymentMethod is not null && PaymentMethods.RequiresCapture(paymentMethod))
                {
                    QueueSettlementCommand(orderId, settlementAction, targetStatus, correlationId);
                    await dbContext.SaveChangesAsync(ct);
                }
                else if (settlementAction == OrderSettlementAction.Cancel)
                {
                    // Milestone 81: unlike capture, cancellation is not
                    // method-gated - a Pix payment is Captured the moment
                    // it's approved, so cancelling it has to refund, not
                    // void a hold that was never placed. Payments decides
                    // which of the two from the payment's own state
                    // (Payment.TryCancel); this path always queues the
                    // command, unconditionally.
                    QueueSettlementCommand(orderId, settlementAction, targetStatus, correlationId);
                    await QueueInventoryCompensationAsync(orderId, previousStatus, correlationId, ct);
                    await dbContext.SaveChangesAsync(ct);
                }

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
    private async Task<(string? PreviousStatus, string? PaymentMethod)> TryTransitionRowAsync(
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
            return (null, null);
        }

        var previousStatus = reader.GetString(0);
        var paymentMethod = await reader.IsDBNullAsync(1, cancellationToken) ? null : reader.GetString(1);
        return (previousStatus, paymentMethod);
    }

    /// <summary>
    /// Milestone 81: the inventory side of cancelling, chosen from where
    /// the order actually was, not guessed:
    ///
    /// <list type="bullet">
    /// <item>Confirmed/Picking/FulfillmentHold: the reservation was
    /// already committed (drawn permanently out of stock) - the units come
    /// back via the same restock command a return uses.</item>
    /// <item>Backordered: nothing was ever reserved, only a place in the
    /// FIFO queue - that place is what needs to be given up.</item>
    /// <item>Created: not reachable from a cancellation an operator drives
    /// through this path in the ordinary case, and if it happens anyway
    /// (an operator cancelling an order the saga is still mid-flight on),
    /// this deliberately does nothing - the saga's own reservation state
    /// lives in Orders.Worker, not here, and guessing at inventory this
    /// repository cannot see would risk conjuring or destroying stock that
    /// belongs to a decision already in flight elsewhere. That race
    /// predates this milestone and remains an open gap, not a regression.</item>
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
            // multi-SKU return before this milestone; fixed there too.
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
