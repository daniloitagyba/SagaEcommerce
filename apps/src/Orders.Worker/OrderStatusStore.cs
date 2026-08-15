using System.Diagnostics;
using System.Text.Json;
using BuildingBlocks;
using Npgsql;
using NpgsqlTypes;
using Polly;
using Polly.Registry;

namespace Orders.Worker;

public enum StatusTransitionResult
{
    /// <summary>The row moved, and this caller is the one that moved it.</summary>
    Transitioned,

    /// <summary>The move is legal in principle, but the row was not in an allowed state - already settled, or someone else won the race.</summary>
    NotApplicable,

    /// <summary>The move is not in the transition table at all.</summary>
    IllegalTransition
}

public sealed class OrderStatusStore(
    NpgsqlDataSource dataSource,
    CouponRedemptionStore couponRedemptionStore,
    PaymentSettlementRequester settlementRequester,
    CustomerTierStore customerTierStore,
    ResiliencePipelineProvider<string> pipelineProvider)
{
    // `status = ANY(@allowed_from)` guards the whole set of
    // legal predecessors in one statement, since an order can now be
    // cancelled from four different states - read-then-write would
    // reintroduce the race the CAS exists to remove. RETURNING carries what
    // the follow-up actions need, so they fire for exactly the winner of
    // the compare-and-set and a loser cannot double-count.
    //
    // The `previous` CTE (FOR UPDATE, same lock and shape as
    // EfOrderStatusRepository's identical TransitionSql) is what makes the
    // row's status *before* this write available to ApplySideEffectsAsync -
    // a plain UPDATE ... RETURNING only ever returns the post-write row,
    // and status is the one column this statement changes, so a bare
    // RETURNING could never tell "was Confirmed" from "was Created" apart.
    // Still one round trip, still atomic: the allowed_from guard lives in
    // the UPDATE's own WHERE, same as before, not the CTE's - a row whose
    // status has since moved out of allowed_from re-evaluates to "no match"
    // the moment the lock is granted, which is what stops a lost update.
    private const string UpdateSql = """
        WITH previous AS (
            SELECT status, coupon_code, payment_method, customer_id, amount_cents FROM orders WHERE id = @id FOR UPDATE
        )
        UPDATE orders o
        SET status = @status
        FROM previous
        WHERE o.id = @id AND previous.status = ANY(@allowed_from)
        RETURNING previous.status, previous.coupon_code, previous.payment_method, previous.customer_id, previous.amount_cents;
        """;

    // Same table, same database, same transaction as the status CAS above -
    // see EfOrderStatusRepository's identical write for why this is needed:
    // the read-model projection only ever learned an order's status from
    // OrderCreated/PaymentDecided otherwise, and PaymentDecided is not even
    // produced in Saga:Mode=Orchestration (the deployed default), so this
    // saga-driven path - Confirmed, Cancelled, Backordered, FulfillmentHold -
    // is actually the high-volume source of status changes the projection
    // was missing entirely, not an edge case.
    private const string InsertOutboxSql = """
        INSERT INTO outbox_messages
            (id, event_type, payload, occurred_at, correlation_id, trace_parent, trace_state, attempt_count, next_attempt_at)
        VALUES
            (@id, @event_type, @payload, @occurred_at, @correlation_id, @trace_parent, @trace_state, 0, @occurred_at);
        """;

    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);
    private readonly ResiliencePipeline _pipeline = pipelineProvider.GetPipeline(ResilienceExtensions.PostgresPipeline);

    public Task<bool> TryConfirmAsync(Guid orderId, string correlationId, CancellationToken cancellationToken)
        => TransitionOrFalseAsync(orderId, OrderStatuses.Confirmed, correlationId, cancellationToken);

    public Task<bool> TryCancelAsync(Guid orderId, string correlationId, CancellationToken cancellationToken)
        => TransitionOrFalseAsync(orderId, OrderStatuses.Cancelled, correlationId, cancellationToken);

    /// <summary>The fulfilment states an operator or warehouse system drives.</summary>
    public Task<StatusTransitionResult> TryTransitionAsync(
        Guid orderId,
        string targetStatus,
        string correlationId,
        CancellationToken cancellationToken)
        => TransitionAsync(orderId, targetStatus, correlationId, cancellationToken);

    private async Task<bool> TransitionOrFalseAsync(
        Guid orderId,
        string targetStatus,
        string correlationId,
        CancellationToken cancellationToken)
        => await TransitionAsync(orderId, targetStatus, correlationId, cancellationToken) == StatusTransitionResult.Transitioned;

    private async Task<StatusTransitionResult> TransitionAsync(
        Guid orderId,
        string targetStatus,
        string correlationId,
        CancellationToken cancellationToken)
    {
        var allowedFrom = OrderStatuses.PredecessorsOf(targetStatus);
        if (allowedFrom.Count == 0)
        {
            return StatusTransitionResult.IllegalTransition;
        }

        var transitioned = await _pipeline.ExecuteAsync(async ct =>
        {
            await using var connection = await dataSource.OpenConnectionAsync(ct);
            await using var transaction = await connection.BeginTransactionAsync(ct);

            var transition = await TryTransitionRowAsync(
                connection,
                transaction,
                orderId,
                targetStatus,
                allowedFrom,
                ct);

            if (!transition.Transitioned)
            {
                await transaction.RollbackAsync(ct);
                return false;
            }

            await ApplySideEffectsAsync(
                connection,
                transaction,
                orderId,
                targetStatus,
                correlationId,
                transition,
                ct);

            await transaction.CommitAsync(ct);
            return true;
        }, cancellationToken);

        if (!transitioned)
        {
            return StatusTransitionResult.NotApplicable;
        }

        return StatusTransitionResult.Transitioned;
    }

    /// <summary>
    /// Applies every database-local consequence before the status transaction
    /// commits. Cross-service commands are persisted to the shared Orders
    /// outbox, so the API's outbox publisher can retry delivery without a
    /// status change ever becoming visible on its own.
    /// </summary>
    private async Task ApplySideEffectsAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid orderId,
        string targetStatus,
        string correlationId,
        TransitionContext context,
        CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;

        // Unconditional - every legal transition this store performs (not
        // just Confirmed/Cancelled below) needs the read model and the
        // event-store timeline to learn about it.
        await QueueOrderStatusChangedAsync(connection, transaction, orderId, targetStatus, correlationId, now, cancellationToken);

        if (targetStatus == OrderStatuses.Confirmed && context.CustomerId is not null)
        {
            await customerTierStore.RecordCompletedOrderAsync(
                connection,
                transaction,
                context.CustomerId,
                context.Amount,
                cancellationToken);
        }

        // The mirror of the block above: a cancellation reaching here from
        // Confirmed, Picking or FulfillmentHold means the order was
        // Confirmed at some earlier point (the state graph guarantees it -
        // those are the only three of Cancelled's legal predecessors
        // reachable only through Confirmed; Created and Backordered are
        // not - see EfOrderStatusRepository.QueueInventoryCompensationAsync's
        // identical check for the same reasoning) and RecordCompletedOrderAsync
        // already ran for it, crediting lifetime_spend permanently even
        // though the order never completed.
        if (targetStatus == OrderStatuses.Cancelled
            && context.CustomerId is not null
            && context.PreviousStatus is OrderStatuses.Confirmed or OrderStatuses.Picking or OrderStatuses.FulfillmentHold)
        {
            await customerTierStore.ReverseCompletedOrderAsync(
                connection,
                transaction,
                context.CustomerId,
                context.Amount,
                cancellationToken);
        }

        if (context.CouponCode is not null)
        {
            var settleCoupon = targetStatus switch
            {
                OrderStatuses.Confirmed => couponRedemptionStore.TryConfirmAsync(
                    connection, transaction, context.CouponCode, orderId, now, cancellationToken),
                OrderStatuses.Cancelled => couponRedemptionStore.TryReleaseAsync(
                    connection, transaction, context.CouponCode, orderId, now, cancellationToken),
                _ => null
            };

            if (settleCoupon is not null)
            {
                await settleCoupon;
            }
        }

        if (context.PaymentMethod is null)
        {
            return;
        }

        if (targetStatus == OrderStatuses.Shipped && PaymentMethods.RequiresCapture(context.PaymentMethod))
        {
            await settlementRequester.RequestCaptureAsync(
                connection,
                transaction,
                orderId,
                correlationId,
                now,
                cancellationToken);
        }
        else if (targetStatus == OrderStatuses.Cancelled)
        {
            await settlementRequester.RequestCancellationAsync(
                connection,
                transaction,
                orderId,
                correlationId,
                "order cancelled",
                now,
                cancellationToken);
        }
    }

    private static async Task QueueOrderStatusChangedAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid orderId,
        string targetStatus,
        string correlationId,
        DateTimeOffset occurredAt,
        CancellationToken cancellationToken)
    {
        var statusChanged = new OrderStatusChanged(Guid.NewGuid(), orderId, targetStatus, occurredAt, correlationId);
        var payload = JsonSerializer.Serialize(statusChanged, SerializerOptions);

        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = InsertOutboxSql;
        command.Parameters.AddWithValue("id", NpgsqlDbType.Uuid, Guid.NewGuid());
        command.Parameters.AddWithValue("event_type", NpgsqlDbType.Varchar, nameof(OrderStatusChanged));
        command.Parameters.AddWithValue("payload", NpgsqlDbType.Jsonb, payload);
        command.Parameters.AddWithValue("occurred_at", NpgsqlDbType.TimestampTz, occurredAt);
        command.Parameters.AddWithValue("correlation_id", NpgsqlDbType.Varchar, correlationId);
        command.Parameters.AddWithValue("trace_parent", NpgsqlDbType.Varchar, (object?)Activity.Current?.Id ?? DBNull.Value);
        command.Parameters.AddWithValue("trace_state", NpgsqlDbType.Varchar, (object?)Activity.Current?.TraceStateString ?? DBNull.Value);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task<TransitionContext> TryTransitionRowAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid orderId,
        string targetStatus,
        IReadOnlyList<string> allowedFrom,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = UpdateSql;
        command.Parameters.AddWithValue("status", NpgsqlDbType.Varchar, targetStatus);
        command.Parameters.AddWithValue("id", NpgsqlDbType.Uuid, orderId);
        command.Parameters.AddWithValue("allowed_from", NpgsqlDbType.Array | NpgsqlDbType.Varchar, allowedFrom.ToArray());

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return TransitionContext.NotTransitioned;
        }

        return new TransitionContext(
            true,
            reader.GetString(0),
            await reader.IsDBNullAsync(1, cancellationToken) ? null : reader.GetString(1),
            await reader.IsDBNullAsync(2, cancellationToken) ? null : reader.GetString(2),
            await reader.IsDBNullAsync(3, cancellationToken) ? null : reader.GetString(3),
            reader.GetInt64(4) / 100m);
    }

    private sealed record TransitionContext(
        bool Transitioned,
        string? PreviousStatus,
        string? CouponCode,
        string? PaymentMethod,
        string? CustomerId,
        decimal Amount)
    {
        public static readonly TransitionContext NotTransitioned = new(false, null, null, null, null, 0m);
    }
}
