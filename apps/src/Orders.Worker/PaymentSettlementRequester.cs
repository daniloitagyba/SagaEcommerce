using System.Diagnostics;
using System.Text.Json;
using BuildingBlocks;
using Npgsql;
using NpgsqlTypes;

namespace Orders.Worker;

public sealed class PaymentSettlementRequester
{
    private const string InsertOutboxSql = """
        INSERT INTO outbox_messages
            (id, event_type, payload, occurred_at, correlation_id, trace_parent, trace_state,
             attempt_count, next_attempt_at, processed_at, last_error)
        VALUES
            (@id, @event_type, @payload::jsonb, @occurred_at, @correlation_id, @trace_parent,
             @trace_state, 0, @occurred_at, NULL, NULL);
        """;

    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);

    public Task RequestCaptureAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid orderId,
        string correlationId,
        DateTimeOffset occurredAt,
        CancellationToken cancellationToken)
    {
        var request = new PaymentCaptureRequested(orderId, correlationId, occurredAt);
        return QueueAsync(connection, transaction, nameof(PaymentCaptureRequested), request, correlationId, occurredAt, cancellationToken);
    }

    public Task RequestCancellationAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid orderId,
        string correlationId,
        string reason,
        DateTimeOffset occurredAt,
        CancellationToken cancellationToken)
    {
        var request = new PaymentCancellationRequested(orderId, reason, correlationId, occurredAt);
        return QueueAsync(connection, transaction, nameof(PaymentCancellationRequested), request, correlationId, occurredAt, cancellationToken);
    }

    private static async Task QueueAsync<TRequest>(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string eventType,
        TRequest request,
        string correlationId,
        DateTimeOffset occurredAt,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = InsertOutboxSql;
        command.Parameters.AddWithValue("id", NpgsqlDbType.Uuid, Guid.NewGuid());
        command.Parameters.AddWithValue("event_type", NpgsqlDbType.Varchar, eventType);
        command.Parameters.AddWithValue("payload", NpgsqlDbType.Jsonb, JsonSerializer.Serialize(request, SerializerOptions));
        command.Parameters.AddWithValue("occurred_at", NpgsqlDbType.TimestampTz, occurredAt);
        command.Parameters.AddWithValue("correlation_id", NpgsqlDbType.Varchar, correlationId);
        command.Parameters.AddWithValue("trace_parent", NpgsqlDbType.Varchar, (object?)Activity.Current?.Id ?? DBNull.Value);
        command.Parameters.AddWithValue("trace_state", NpgsqlDbType.Varchar, (object?)Activity.Current?.TraceStateString ?? DBNull.Value);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }
}
