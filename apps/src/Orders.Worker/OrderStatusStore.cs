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

    /// <summary>The move is legal in principle, but the row was not in an allowed state.</summary>
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

    private const string InsertOutboxSql = """
        INSERT INTO outbox_messages
            (id, event_type, payload, occurred_at, correlation_id, trace_parent, trace_state, attempt_count, next_attempt_at)
        VALUES
            (@id, @event_type, @payload, @occurred_at, @correlation_id, @trace_parent, @trace_state, 0, @occurred_at);
        """;

    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);
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
    /// Transitions an order in an existing transaction.
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
    /// Applies local effects for an order transition.
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
    /// Allocates the next order event version.
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
