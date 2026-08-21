using System.Diagnostics;
using System.Text.Json;
using BuildingBlocks;
using Npgsql;
using NpgsqlTypes;
using Orders.Application.Ports;
using Polly;
using Polly.Registry;

namespace Orders.Infrastructure.Persistence;

public sealed class OrderTransitionExecutor(
    NpgsqlDataSource dataSource,
    ResiliencePipelineProvider<string> pipelineProvider,
    TimeProvider? timeProvider = null)
{
    private const string TransitionSql = """
        WITH previous AS (
            SELECT status, coupon_code, payment_method, customer_id, amount_cents, campaign_code FROM orders WHERE id = @id FOR UPDATE
        )
        UPDATE orders o
        SET status = @status
        FROM previous
        WHERE o.id = @id AND previous.status = ANY(@allowed_from)
        RETURNING previous.status, previous.coupon_code, previous.payment_method, previous.customer_id, previous.amount_cents, previous.campaign_code;
        """;

    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);
    private readonly ResiliencePipeline _pipeline = pipelineProvider.GetPipeline(ResilienceExtensions.PostgresTransactionPipeline);
    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;

    public async Task<OrderTransition> TryTransitionAsync(
        Guid orderId,
        string targetStatus,
        IReadOnlyList<string> allowedFrom,
        string correlationId,
        bool includeCancellationCompensation,
        CancellationToken cancellationToken)
    {
        if (allowedFrom.Count == 0)
        {
            return new OrderTransition(OrderTransitionOutcome.NotApplicable, null);
        }

        return await _pipeline.ExecuteAsync(async ct =>
        {
            await using var connection = await dataSource.OpenConnectionAsync(ct);
            await using var transaction = await connection.BeginTransactionAsync(ct);
            var result = await TryTransitionWithinTransactionAsync(
                connection, transaction, orderId, targetStatus, allowedFrom, correlationId,
                includeCancellationCompensation, ct);

            if (result.Outcome != OrderTransitionOutcome.Advanced)
            {
                await transaction.RollbackAsync(ct);
                return result;
            }

            await transaction.CommitAsync(ct);
            return result;
        }, cancellationToken);
    }

    public async Task<OrderTransition> TryTransitionWithinTransactionAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid orderId,
        string targetStatus,
        IReadOnlyList<string> allowedFrom,
        string correlationId,
        bool includeCancellationCompensation,
        CancellationToken cancellationToken)
    {
        var context = await TryTransitionRowAsync(
            connection, transaction, orderId, targetStatus, allowedFrom, cancellationToken);
        if (!context.Transitioned)
        {
            return new OrderTransition(
                await ExistsAsync(connection, transaction, orderId, cancellationToken)
                    ? OrderTransitionOutcome.NotApplicable
                    : OrderTransitionOutcome.NotFound,
                null);
        }

        var now = _timeProvider.GetUtcNow();
        await QueueOutboxAsync(
            connection, transaction, nameof(OrderStatusChanged),
            new OrderStatusChanged(Guid.NewGuid(), orderId, targetStatus, now, correlationId,
                await NextEventVersionAsync(connection, transaction, cancellationToken)),
            correlationId, now, cancellationToken);

        if (targetStatus == OrderStatuses.Confirmed)
        {
            await ConfirmCouponAsync(connection, transaction, orderId, now, cancellationToken);
            await ConfirmCampaignAsync(connection, transaction, orderId, now, cancellationToken);
            await RecordCustomerStandingAsync(connection, transaction, context.CustomerId, context.Amount, cancellationToken);
        }

        if (targetStatus == OrderStatuses.Cancelled)
        {
            await ReleaseCouponAsync(connection, transaction, orderId, now, cancellationToken);
            await ReleaseCampaignAsync(connection, transaction, orderId, now, cancellationToken);
            if (context.PreviousStatus is OrderStatuses.Confirmed or OrderStatuses.Picking or OrderStatuses.FulfillmentHold)
            {
                await ReverseCustomerStandingAsync(connection, transaction, context.CustomerId, context.Amount, cancellationToken);
            }

            if (includeCancellationCompensation)
            {
                await QueueCancellationCompensationAsync(connection, transaction, orderId, context.PreviousStatus, correlationId, now, cancellationToken);
            }
        }

        if (targetStatus == OrderStatuses.Shipped && PaymentMethods.RequiresCapture(context.PaymentMethod ?? string.Empty))
        {
            await QueueOutboxAsync(connection, transaction, nameof(PaymentCaptureRequested),
                new PaymentCaptureRequested(orderId, correlationId, now), correlationId, now, cancellationToken);
        }
        else if (targetStatus == OrderStatuses.Cancelled && context.PaymentMethod is not null)
        {
            await QueueOutboxAsync(connection, transaction, nameof(PaymentCancellationRequested),
                new PaymentCancellationRequested(orderId, "order cancelled", correlationId, now), correlationId, now, cancellationToken);
        }

        return new OrderTransition(OrderTransitionOutcome.Advanced, context.PaymentMethod);
    }

    private static async Task<TransitionContext> TryTransitionRowAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, Guid orderId, string targetStatus, IReadOnlyList<string> allowedFrom, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = TransitionSql;
        command.Parameters.AddWithValue("id", NpgsqlDbType.Uuid, orderId);
        command.Parameters.AddWithValue("status", NpgsqlDbType.Varchar, targetStatus);
        command.Parameters.AddWithValue("allowed_from", NpgsqlDbType.Array | NpgsqlDbType.Varchar, allowedFrom.ToArray());
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return TransitionContext.NotTransitioned;
        }

        return new TransitionContext(
            true, reader.GetString(0),
            await reader.IsDBNullAsync(1, cancellationToken) ? null : reader.GetString(1),
            await reader.IsDBNullAsync(2, cancellationToken) ? null : reader.GetString(2),
            await reader.IsDBNullAsync(3, cancellationToken) ? null : reader.GetString(3),
            reader.GetInt64(4) / 100m,
            await reader.IsDBNullAsync(5, cancellationToken) ? null : reader.GetString(5));
    }

    private static async Task<bool> ExistsAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, Guid orderId, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT EXISTS(SELECT 1 FROM orders WHERE id = @id)";
        command.Parameters.AddWithValue("id", NpgsqlDbType.Uuid, orderId);
        return (bool)(await command.ExecuteScalarAsync(cancellationToken))!;
    }

    private static async Task<long> NextEventVersionAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT nextval('order_event_version_seq')";
        return (long)(await command.ExecuteScalarAsync(cancellationToken))!;
    }

    private static async Task QueueOutboxAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, string eventType, object payload, string correlationId, DateTimeOffset occurredAt, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO outbox_messages (id, event_type, payload, occurred_at, correlation_id, trace_parent, trace_state, attempt_count, next_attempt_at)
            VALUES (@id, @event_type, @payload, @occurred_at, @correlation_id, @trace_parent, @trace_state, 0, @occurred_at);
            """;
        command.Parameters.AddWithValue("id", NpgsqlDbType.Uuid, Guid.NewGuid());
        command.Parameters.AddWithValue("event_type", NpgsqlDbType.Varchar, eventType);
        command.Parameters.AddWithValue("payload", NpgsqlDbType.Jsonb, JsonSerializer.Serialize(payload, SerializerOptions));
        command.Parameters.AddWithValue("occurred_at", NpgsqlDbType.TimestampTz, occurredAt);
        command.Parameters.AddWithValue("correlation_id", NpgsqlDbType.Varchar, correlationId);
        command.Parameters.AddWithValue("trace_parent", NpgsqlDbType.Varchar, (object?)Activity.Current?.Id ?? DBNull.Value);
        command.Parameters.AddWithValue("trace_state", NpgsqlDbType.Varchar, (object?)Activity.Current?.TraceStateString ?? DBNull.Value);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static Task ConfirmCouponAsync(NpgsqlConnection c, NpgsqlTransaction t, Guid id, DateTimeOffset now, CancellationToken ct) => ExecuteAsync(c, t, "UPDATE coupon_redemptions SET state = 'Confirmed', settled_at = @now WHERE order_id = @id AND state = 'Reserved'", id, now, ct);
    private static Task ConfirmCampaignAsync(NpgsqlConnection c, NpgsqlTransaction t, Guid id, DateTimeOffset now, CancellationToken ct) => ExecuteAsync(c, t, "UPDATE promotion_campaign_claims SET state = 'Confirmed', settled_at = @now WHERE order_id = @id AND state = 'Reserved'", id, now, ct);
    private static Task ReleaseCouponAsync(NpgsqlConnection c, NpgsqlTransaction t, Guid id, DateTimeOffset now, CancellationToken ct) => ExecuteAsync(c, t, """WITH released AS (UPDATE coupon_redemptions SET state = 'Released', settled_at = @now WHERE order_id = @id AND state IN ('Reserved', 'Confirmed') RETURNING code) UPDATE coupons SET redemption_count = GREATEST(redemption_count - 1, 0) FROM released WHERE coupons.code = released.code;""", id, now, ct);
    private static Task ReleaseCampaignAsync(NpgsqlConnection c, NpgsqlTransaction t, Guid id, DateTimeOffset now, CancellationToken ct) => ExecuteAsync(c, t, """WITH released AS (UPDATE promotion_campaign_claims SET state = 'Released', settled_at = @now WHERE order_id = @id AND state IN ('Reserved', 'Confirmed') RETURNING code, amount) UPDATE promotion_campaigns SET budget_remaining = LEAST(budget_remaining + released.amount, total_budget) FROM released WHERE promotion_campaigns.code = released.code;""", id, now, ct);

    private static async Task ExecuteAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, string sql, Guid orderId, DateTimeOffset now, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        command.Parameters.AddWithValue("id", NpgsqlDbType.Uuid, orderId);
        command.Parameters.AddWithValue("now", NpgsqlDbType.TimestampTz, now);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static Task RecordCustomerStandingAsync(NpgsqlConnection c, NpgsqlTransaction t, string? customerId, decimal amount, CancellationToken ct) => UpdateCustomerStandingAsync(c, t, "UPDATE customers SET lifetime_spend = lifetime_spend + @amount, completed_order_count = completed_order_count + 1, tier = CASE WHEN lifetime_spend + @amount >= 5000 THEN 'Gold' WHEN lifetime_spend + @amount >= 1000 THEN 'Silver' ELSE 'Bronze' END WHERE id = @customer_id", customerId, amount, ct);
    private static Task ReverseCustomerStandingAsync(NpgsqlConnection c, NpgsqlTransaction t, string? customerId, decimal amount, CancellationToken ct) => UpdateCustomerStandingAsync(c, t, "UPDATE customers SET lifetime_spend = GREATEST(lifetime_spend - @amount, 0), completed_order_count = GREATEST(completed_order_count - 1, 0) WHERE id = @customer_id", customerId, amount, ct);

    private static async Task UpdateCustomerStandingAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, string sql, string? customerId, decimal amount, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(customerId)) return;
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        command.Parameters.AddWithValue("customer_id", NpgsqlDbType.Varchar, customerId);
        command.Parameters.AddWithValue("amount", NpgsqlDbType.Numeric, amount);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task QueueCancellationCompensationAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, Guid orderId, string? previousStatus, string correlationId, DateTimeOffset now, CancellationToken cancellationToken)
    {
        if (previousStatus is OrderStatuses.Created or OrderStatuses.Backordered)
        {
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = "UPDATE saga_orchestration_states SET cancellation_requested_at = COALESCE(cancellation_requested_at, @now) WHERE order_id = @id";
            command.Parameters.AddWithValue("id", NpgsqlDbType.Uuid, orderId);
            command.Parameters.AddWithValue("now", NpgsqlDbType.TimestampTz, now);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        if (previousStatus == OrderStatuses.Backordered)
        {
            await QueueOutboxAsync(connection, transaction, nameof(BackorderCancellationRequested), new BackorderCancellationRequested(orderId, correlationId, now), correlationId, now, cancellationToken);
            return;
        }

        if (previousStatus is not (OrderStatuses.Confirmed or OrderStatuses.Picking or OrderStatuses.FulfillmentHold))
        {
            return;
        }

        await using var linesCommand = connection.CreateCommand();
        linesCommand.Transaction = transaction;
        linesCommand.CommandText = "SELECT sku, quantity FROM order_lines WHERE order_id = @id";
        linesCommand.Parameters.AddWithValue("id", NpgsqlDbType.Uuid, orderId);
        await using var reader = await linesCommand.ExecuteReaderAsync(cancellationToken);
        var lines = new List<(string Sku, int Quantity)>();
        while (await reader.ReadAsync(cancellationToken))
        {
            lines.Add((reader.GetString(0), reader.GetInt32(1)));
        }
        await reader.DisposeAsync();

        foreach (var line in lines)
        {
            await QueueOutboxAsync(
                connection,
                transaction,
                nameof(InventoryRestockRequested),
                new InventoryRestockRequested(Guid.NewGuid(), orderId, line.Sku, line.Quantity, correlationId, now),
                correlationId,
                now,
                cancellationToken);
        }
    }

    private sealed record TransitionContext(bool Transitioned, string? PreviousStatus, string? CouponCode, string? PaymentMethod, string? CustomerId, decimal Amount, string? CampaignCode)
    {
        public static readonly TransitionContext NotTransitioned = new(false, null, null, null, null, 0m, null);
    }
}
