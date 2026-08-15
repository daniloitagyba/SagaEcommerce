using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Npgsql;
using NpgsqlTypes;
using Polly;
using Polly.Registry;

namespace BuildingBlocks;

// The dedup half of every outbox-backed consumer in this codebase: an
// INSERT ... ON CONFLICT (consumer_name, event_id) DO NOTHING against
// inbox_messages, so a redelivered Kafka message (the standard
// at-least-once contract every KafkaConsumerHost carries) is processed at
// most once. Previously written out as raw SQL 8 separate times across
// Inventory.Service, Payments.Service and Orders.Worker; the SQL text now
// lives here, once.
//
// Two variants, not one, because there are genuinely two different call
// shapes over that same statement in this codebase - forcing either
// through the other would change behavior, not just move code:
//
//   - TryRecordAsync (instance method): Orders.Worker's shape. Runs on its
//     own connection, outside any transaction, dedup'd purely on the
//     Kafka offset (consumer_name, event_id) pair - correct for consumers
//     whose business write and inbox record aren't part of one atomic
//     unit.
//   - TryRecordWithinTransactionAsync (static): Inventory.Service's and
//     Payments.Service's shape. Runs via DatabaseFacade.ExecuteSqlInterpolatedAsync
//     against the caller's own already-open EF transaction, so a rolled-back
//     business write also rolls back the inbox record instead of leaving
//     a "processed" marker for a change that never actually happened.
public sealed class InboxStore(NpgsqlDataSource dataSource, ResiliencePipelineProvider<string> pipelineProvider)
{
    private const string InsertSql = """
        INSERT INTO inbox_messages
            (consumer_name, event_id, topic, partition, "offset", correlation_id, processed_at)
        VALUES
            (@consumer_name, @event_id, @topic, @partition, @offset, @correlation_id, @processed_at)
        ON CONFLICT (consumer_name, event_id) DO NOTHING;
        """;

    private readonly ResiliencePipeline _pipeline = pipelineProvider.GetPipeline(ResilienceExtensions.PostgresPipeline);

    public async Task<bool> TryRecordAsync(
        string consumerName,
        Guid eventId,
        string topic,
        int partition,
        long offset,
        string correlationId,
        DateTimeOffset processedAt,
        CancellationToken cancellationToken)
    {
        return await _pipeline.ExecuteAsync(async ct =>
        {
            await using var command = dataSource.CreateCommand(InsertSql);
            command.Parameters.AddWithValue("consumer_name", NpgsqlDbType.Varchar, consumerName);
            command.Parameters.AddWithValue("event_id", NpgsqlDbType.Uuid, eventId);
            command.Parameters.AddWithValue("topic", NpgsqlDbType.Varchar, topic);
            command.Parameters.AddWithValue("partition", NpgsqlDbType.Integer, partition);
            command.Parameters.AddWithValue("offset", NpgsqlDbType.Bigint, offset);
            command.Parameters.AddWithValue("correlation_id", NpgsqlDbType.Varchar, correlationId);
            command.Parameters.AddWithValue("processed_at", NpgsqlDbType.TimestampTz, processedAt);

            return await command.ExecuteNonQueryAsync(ct) == 1;
        }, cancellationToken);
    }

    /// <summary>Returns the number of rows inserted - 0 means this (consumer_name, event_id) pair was already recorded, i.e. a duplicate.</summary>
    public static Task<int> TryRecordWithinTransactionAsync(
        DatabaseFacade database,
        string consumerName,
        Guid eventId,
        string topic,
        int partition,
        long offset,
        string correlationId,
        DateTimeOffset processedAt,
        CancellationToken cancellationToken)
    {
        return database.ExecuteSqlInterpolatedAsync(
            $"""
            INSERT INTO inbox_messages (consumer_name, event_id, topic, partition, "offset", correlation_id, processed_at)
            VALUES ({consumerName}, {eventId}, {topic}, {partition}, {offset}, {correlationId}, {processedAt})
            ON CONFLICT (consumer_name, event_id) DO NOTHING
            """,
            cancellationToken);
    }
}
