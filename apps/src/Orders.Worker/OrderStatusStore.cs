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
    PromotionCampaignStore promotionCampaignStore,
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
            SELECT status, coupon_code, payment_method, customer_id, amount_cents, campaign_code FROM orders WHERE id = @id FOR UPDATE
        )
        UPDATE orders o
        SET status = @status
        FROM previous
        WHERE o.id = @id AND previous.status = ANY(@allowed_from)
        RETURNING previous.status, previous.coupon_code, previous.payment_method, previous.customer_id, previous.amount_cents, previous.campaign_code;
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
    // No-retry transactional pipeline, not the retrying PostgresPipeline -
    // see ResilienceExtensions.PostgresTransactionPipeline's own comment.
    // TransitionAsync's single write is exactly the shape that pipeline
    // exists for: a lost commit ack retried blindly would mask a genuinely
    // successful transition as a NotApplicable result to this store's own
    // caller.
    private readonly ResiliencePipeline _pipeline = pipelineProvider.GetPipeline(ResilienceExtensions.PostgresTransactionPipeline);

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
        if (OrderStatuses.PredecessorsOf(targetStatus).Count == 0)
        {
            return StatusTransitionResult.IllegalTransition;
        }

        var transitioned = await _pipeline.ExecuteAsync(async ct =>
        {
            await using var connection = await dataSource.OpenConnectionAsync(ct);
            await using var transaction = await connection.BeginTransactionAsync(ct);

            var result = await TryTransitionWithinTransactionAsync(
                connection, transaction, orderId, targetStatus, correlationId, ct);

            if (!result)
            {
                await transaction.RollbackAsync(ct);
                return false;
            }

            await transaction.CommitAsync(ct);
            return true;
        }, cancellationToken);

        return transitioned ? StatusTransitionResult.Transitioned : StatusTransitionResult.NotApplicable;
    }

    /// <summary>
    /// The same CAS-guarded transition TransitionAsync performs, but
    /// against a connection/transaction the caller already owns rather
    /// than one this store opens and commits itself - so a status change
    /// can be folded into the same commit as whatever durable state made
    /// it necessary in the first place.
    ///
    /// Introduced at Milestone 91 for
    /// SagaOrchestrationStore.ClaimTimedOutAndQueueAsync and
    /// TryCompleteAndQueueAsync: before this, a saga row's deletion (the
    /// durable claim that inventory has been released) committed in one
    /// transaction, and the order's own status transition ran afterwards
    /// in a second, separate one. A crash between the two left the saga
    /// row permanently gone - so the order could never time out again -
    /// with the order itself stuck in whatever non-terminal status it was
    /// already in. See docs/roadmap-milestones-91-99.md, "durable claim,
    /// non-durable act".
    /// </summary>
    public async Task<bool> TryTransitionWithinTransactionAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid orderId,
        string targetStatus,
        string correlationId,
        CancellationToken cancellationToken)
    {
        var allowedFrom = OrderStatuses.PredecessorsOf(targetStatus);
        if (allowedFrom.Count == 0)
        {
            return false;
        }

        var transition = await TryTransitionRowAsync(
            connection, transaction, orderId, targetStatus, allowedFrom, cancellationToken);

        if (!transition.Transitioned)
        {
            return false;
        }

        await ApplySideEffectsAsync(
            connection, transaction, orderId, targetStatus, correlationId, transition, cancellationToken);

        return true;
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

        if (context.CampaignCode is not null)
        {
            var settleCampaign = targetStatus switch
            {
                OrderStatuses.Confirmed => promotionCampaignStore.TryConfirmAsync(
                    connection, transaction, context.CampaignCode, orderId, now, cancellationToken),
                OrderStatuses.Cancelled => promotionCampaignStore.TryReleaseAsync(
                    connection, transaction, context.CampaignCode, orderId, now, cancellationToken),
                _ => null
            };

            if (settleCampaign is not null)
            {
                await settleCampaign;
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
        var version = await NextEventVersionAsync(connection, transaction, cancellationToken);
        var statusChanged = new OrderStatusChanged(Guid.NewGuid(), orderId, targetStatus, occurredAt, correlationId, version);
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

    /// <summary>
    /// Allocates the next value from order_event_version_seq - a single
    /// Postgres sequence shared with EfOrderStatusRepository/
    /// EfOrderReturnRepository's identical calls in Orders.Infrastructure,
    /// which is what makes it a cross-process monotonic counter for
    /// OrderStatusChanged rather than just a per-store one. nextval() is
    /// atomic and takes no lock a concurrent writer could contend on, so
    /// calling it from inside this same transaction costs nothing beyond
    /// the round trip. See OrderStatusChanged's own class comment.
    /// </summary>
    private static async Task<long> NextEventVersionAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT nextval('order_event_version_seq')";
        return (long)(await command.ExecuteScalarAsync(cancellationToken))!;
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
            reader.GetInt64(4) / 100m,
            await reader.IsDBNullAsync(5, cancellationToken) ? null : reader.GetString(5));
    }

    private sealed record TransitionContext(
        bool Transitioned,
        string? PreviousStatus,
        string? CouponCode,
        string? PaymentMethod,
        string? CustomerId,
        decimal Amount,
        string? CampaignCode = null)
    {
        public static readonly TransitionContext NotTransitioned = new(false, null, null, null, null, 0m, null);
    }
}
