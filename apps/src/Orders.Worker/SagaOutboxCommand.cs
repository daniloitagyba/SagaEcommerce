using System.Diagnostics;
using System.Text.Json;
using Npgsql;
using NpgsqlTypes;

namespace Orders.Worker;

/// <summary>
/// Represents a persisted saga command.
/// </summary>
public sealed record SagaOutboxCommand(
    Guid Id,
    Guid OrderId,
    string Topic,
    string Key,
    string Payload,
    string CorrelationId,
    DateTimeOffset OccurredAt,
    string? TraceParent,
    string? TraceState)
{
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);

    public static SagaOutboxCommand Create<T>(
        Guid orderId,
        string topic,
        string key,
        T payload,
        string correlationId,
        DateTimeOffset occurredAt) =>
        new(
            Guid.NewGuid(),
            orderId,
            topic,
            key,
            JsonSerializer.Serialize(payload, SerializerOptions),
            correlationId,
            occurredAt,
            Activity.Current?.Id,
            Activity.Current?.TraceStateString);
}

internal static class SagaOutboxWriter
{
    private const string InsertSql = """
        INSERT INTO saga_outbox_messages
            (id, order_id, topic, message_key, payload, correlation_id, trace_parent, trace_state,
             occurred_at, attempt_count, next_attempt_at, processed_at, last_error)
        VALUES
            (@id, @order_id, @topic, @message_key, @payload::jsonb, @correlation_id, @trace_parent,
             @trace_state, @occurred_at, 0, @occurred_at, NULL, NULL)
        ON CONFLICT (id) DO NOTHING;
        """;

    public static async Task EnqueueAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        IReadOnlyList<SagaOutboxCommand> commands,
        CancellationToken cancellationToken)
    {
        foreach (var item in commands)
        {
            await using var command = new NpgsqlCommand(InsertSql, connection, transaction);
            command.Parameters.AddWithValue("id", NpgsqlDbType.Uuid, item.Id);
            command.Parameters.AddWithValue("order_id", NpgsqlDbType.Uuid, item.OrderId);
            command.Parameters.AddWithValue("topic", NpgsqlDbType.Varchar, item.Topic);
            command.Parameters.AddWithValue("message_key", NpgsqlDbType.Varchar, item.Key);
            command.Parameters.AddWithValue("payload", NpgsqlDbType.Jsonb, item.Payload);
            command.Parameters.AddWithValue("correlation_id", NpgsqlDbType.Varchar, item.CorrelationId);
            command.Parameters.AddWithValue("trace_parent", NpgsqlDbType.Varchar, (object?)item.TraceParent ?? DBNull.Value);
            command.Parameters.AddWithValue("trace_state", NpgsqlDbType.Varchar, (object?)item.TraceState ?? DBNull.Value);
            command.Parameters.AddWithValue("occurred_at", NpgsqlDbType.TimestampTz, item.OccurredAt);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
    }
}
