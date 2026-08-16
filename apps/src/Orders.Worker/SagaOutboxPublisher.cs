using System.Diagnostics;
using System.Text;
using BuildingBlocks;
using Confluent.Kafka;
using Microsoft.Extensions.Options;
using Npgsql;
using NpgsqlTypes;
using Polly;
using Polly.Registry;

namespace Orders.Worker;

/// <summary>
/// Delivers commands durably recorded by the saga store. PostgreSQL owns
/// the retry state; a process crash after Kafka accepts a command but before
/// the row is marked processed can only cause a duplicate, never a loss.
/// Every target consumer is idempotent on its business identifier.
///
/// Claim-then-publish-then-mark, the same three-phase shape
/// BuildingBlocks' own OutboxPublisher&lt;TDbContext&gt; uses (see
/// OutboxPublisher.ClaimBatchAsync) - not the single long transaction this
/// class used before Milestone 91. That transaction held every claimed
/// row's FOR UPDATE lock (and the open transaction they imply) for as long
/// as the whole batch's Kafka publish loop took, which is precisely what
/// OutboxPublisher's own class comment warns never to do: a slow or
/// unreachable broker must never hold Postgres row locks open, since that
/// blocks VACUUM on the Orders database for the duration, not just this
/// table. ClaimBatchAsync below claims by pushing next_attempt_at forward
/// in a transaction that commits immediately; PublishAsync then runs
/// outside any transaction, and MarkPublishedAsync/MarkFailedAsync/
/// MoveToDeadLetterAsync each touch exactly one row in their own short
/// statement. A crash between claim and mark simply makes the row
/// reclaimable again once SagaOrchestrationOptions.OutboxClaimWindowSeconds
/// elapses - the standard at-least-once outbox contract this codebase
/// already relies on everywhere else, not a new failure mode.
/// </summary>
public sealed class SagaOutboxPublisher(
    NpgsqlDataSource dataSource,
    IProducer<string, string> producer,
    IOptions<SagaOrchestrationOptions> options,
    ResiliencePipelineProvider<string> pipelineProvider,
    TimeProvider timeProvider,
    ILogger<SagaOutboxPublisher> logger) : BackgroundService
{
    private readonly SagaOrchestrationOptions _options = options.Value;
    private readonly ResiliencePipeline _kafkaPipeline = pipelineProvider.GetPipeline(ResilienceExtensions.KafkaProducerPipeline);
    // No-retry transactional pipeline for every Postgres call below
    // (claim/mark-published/mark-failed/dead-letter) - see
    // ResilienceExtensions.PostgresTransactionPipeline's own comment. Not
    // the same pipeline as the Kafka produce call: these are a different
    // dependency with a different timeout/retry shape, and before
    // Milestone 91 this whole class incorrectly shared one pipeline
    // (tuned for Kafka) across both.
    private readonly ResiliencePipeline _dbPipeline = pipelineProvider.GetPipeline(ResilienceExtensions.PostgresTransactionPipeline);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            var processed = 0;
            try
            {
                processed = await ProcessBatchAsync(stoppingToken);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                SagaOutboxLog.BatchFailed(logger, exception);
            }

            if (processed == 0)
            {
                await Task.Delay(
                    TimeSpan.FromMilliseconds(_options.OutboxPollIntervalMilliseconds),
                    timeProvider,
                    stoppingToken);
            }
        }
    }

    internal async Task<int> ProcessBatchAsync(CancellationToken cancellationToken)
    {
        var messages = await ClaimBatchAsync(cancellationToken);

        // Outside any transaction from here on - see this class's own
        // comment above for why a slow broker must never hold the claim
        // transaction open across the publish loop.
        foreach (var item in messages)
        {
            await PublishAsync(item, cancellationToken);
        }

        return messages.Count;
    }

    /// <summary>
    /// Claims a batch by pushing each row's next_attempt_at forward by
    /// OutboxClaimWindowSeconds, inside a transaction that commits
    /// immediately - the raw-Npgsql counterpart of
    /// OutboxPublisher.ClaimBatchAsync. FOR UPDATE SKIP LOCKED is what lets
    /// a second replica's own poll skip rows this one just claimed without
    /// blocking on them.
    /// </summary>
    private async Task<List<PendingSagaCommand>> ClaimBatchAsync(CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();

        const string selectSql = """
            SELECT id, order_id, topic, message_key, payload::text, correlation_id,
                   trace_parent, trace_state, attempt_count
            FROM saga_outbox_messages
            WHERE processed_at IS NULL AND next_attempt_at <= @now
            ORDER BY occurred_at
            LIMIT @batch_size
            FOR UPDATE SKIP LOCKED;
            """;

        const string claimSql = """
            UPDATE saga_outbox_messages SET next_attempt_at = @claimed_until WHERE id = ANY(@ids);
            """;

        return await _dbPipeline.ExecuteAsync(async ct =>
        {
            await using var connection = await dataSource.OpenConnectionAsync(ct);
            await using var transaction = await connection.BeginTransactionAsync(ct);
            var messages = new List<PendingSagaCommand>();

            await using (var command = new NpgsqlCommand(selectSql, connection, transaction))
            {
                command.Parameters.AddWithValue("now", NpgsqlDbType.TimestampTz, now);
                command.Parameters.AddWithValue("batch_size", NpgsqlDbType.Integer, _options.OutboxBatchSize);
                await using var reader = await command.ExecuteReaderAsync(ct);
                while (await reader.ReadAsync(ct))
                {
                    messages.Add(new PendingSagaCommand(
                        reader.GetGuid(0),
                        reader.GetGuid(1),
                        reader.GetString(2),
                        reader.GetString(3),
                        reader.GetString(4),
                        reader.GetString(5),
                        await reader.IsDBNullAsync(6, ct) ? null : reader.GetString(6),
                        await reader.IsDBNullAsync(7, ct) ? null : reader.GetString(7),
                        reader.GetInt32(8)));
                }
            }

            if (messages.Count > 0)
            {
                var claimedUntil = now.AddSeconds(_options.OutboxClaimWindowSeconds);
                await using var claimCommand = new NpgsqlCommand(claimSql, connection, transaction);
                claimCommand.Parameters.AddWithValue("claimed_until", NpgsqlDbType.TimestampTz, claimedUntil);
                claimCommand.Parameters.AddWithValue("ids", NpgsqlDbType.Array | NpgsqlDbType.Uuid, messages.Select(m => m.Id).ToArray());
                await claimCommand.ExecuteNonQueryAsync(ct);
            }

            await transaction.CommitAsync(ct);
            return messages;
        }, cancellationToken);
    }

    private async Task PublishAsync(PendingSagaCommand item, CancellationToken cancellationToken)
    {
        using var activity = OrdersTelemetry.StartActivity(
            "saga.outbox.publish", ActivityKind.Producer, item.TraceParent, item.TraceState);
        activity?.SetTag("messaging.system", "kafka");
        activity?.SetTag("messaging.destination.name", item.Topic);
        activity?.SetTag("messaging.message.id", item.Id);
        activity?.SetTag("order.id", item.OrderId);

        try
        {
            var headers = new Headers();
            AddHeader(headers, MessagingHeaders.CorrelationId, item.CorrelationId);
            AddHeader(headers, MessagingHeaders.TraceParent, activity?.Id ?? item.TraceParent);
            AddHeader(headers, MessagingHeaders.TraceState, activity?.TraceStateString ?? item.TraceState);
            var message = new Message<string, string> { Key = item.Key, Value = item.Payload, Headers = headers };

            await _kafkaPipeline.ExecuteAsync(
                async ct => await producer.ProduceAsync(item.Topic, message, ct).WaitAsync(ct),
                cancellationToken);

            await MarkPublishedAsync(item.Id, timeProvider.GetUtcNow(), cancellationToken);
            SagaOutboxLog.Published(logger, item.Id, item.OrderId, item.Topic);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            var attemptCount = item.AttemptCount + 1;
            var error = exception.Message.Length <= 2_000 ? exception.Message : exception.Message[..2_000];

            if (attemptCount >= _options.OutboxMaximumAttempts)
            {
                await MoveToDeadLetterAsync(item, attemptCount, error, cancellationToken);
                SagaOutboxLog.DeadLettered(logger, item.Id, item.OrderId, attemptCount, exception);
                return;
            }

            var exponent = Math.Min(attemptCount - 1, 6);
            var delaySeconds = Math.Min(_options.OutboxMaximumRetryDelaySeconds, 1 << exponent);
            var now = timeProvider.GetUtcNow();
            await MarkFailedAsync(item.Id, attemptCount, now.AddSeconds(delaySeconds), error, cancellationToken);
            SagaOutboxLog.RetryScheduled(logger, item.Id, item.OrderId, attemptCount, exception);
        }
    }

    private async Task MarkPublishedAsync(Guid id, DateTimeOffset now, CancellationToken cancellationToken)
    {
        await _dbPipeline.ExecuteAsync(async ct =>
        {
            await using var command = dataSource.CreateCommand(
                "UPDATE saga_outbox_messages SET processed_at = @now, last_error = NULL WHERE id = @id");
            command.Parameters.AddWithValue("now", NpgsqlDbType.TimestampTz, now);
            command.Parameters.AddWithValue("id", NpgsqlDbType.Uuid, id);
            await command.ExecuteNonQueryAsync(ct);
        }, cancellationToken);
    }

    private async Task MarkFailedAsync(Guid id, int attemptCount, DateTimeOffset nextAttemptAt, string error, CancellationToken cancellationToken)
    {
        await _dbPipeline.ExecuteAsync(async ct =>
        {
            await using var command = dataSource.CreateCommand(
                """
                UPDATE saga_outbox_messages
                SET attempt_count = @attempt_count, next_attempt_at = @next_attempt_at, last_error = @last_error
                WHERE id = @id
                """);
            command.Parameters.AddWithValue("attempt_count", NpgsqlDbType.Integer, attemptCount);
            command.Parameters.AddWithValue("next_attempt_at", NpgsqlDbType.TimestampTz, nextAttemptAt);
            command.Parameters.AddWithValue("last_error", NpgsqlDbType.Varchar, error);
            command.Parameters.AddWithValue("id", NpgsqlDbType.Uuid, id);
            await command.ExecuteNonQueryAsync(ct);
        }, cancellationToken);
    }

    /// <summary>
    /// Moves a command that exhausted SagaOrchestrationOptions.OutboxMaximumAttempts
    /// out of the pending set for good, atomically (one statement: a DELETE
    /// ... RETURNING feeding an INSERT ... SELECT) so the row is never
    /// visible in neither table nor both at once. See OutboxOptions.MaximumAttempts
    /// for the shared outbox's identical reasoning.
    /// </summary>
    private async Task MoveToDeadLetterAsync(PendingSagaCommand item, int attemptCount, string error, CancellationToken cancellationToken)
    {
        const string sql = """
            WITH moved AS (
                DELETE FROM saga_outbox_messages WHERE id = @id
                RETURNING id, order_id, topic, message_key, payload, correlation_id, trace_parent, trace_state, occurred_at
            )
            INSERT INTO saga_outbox_dead_letters
                (id, order_id, topic, message_key, payload, correlation_id, trace_parent, trace_state, occurred_at, attempt_count, last_error, dead_lettered_at)
            SELECT id, order_id, topic, message_key, payload, correlation_id, trace_parent, trace_state, occurred_at, @attempt_count, @last_error, @dead_lettered_at
            FROM moved
            ON CONFLICT (id) DO NOTHING;
            """;

        await _dbPipeline.ExecuteAsync(async ct =>
        {
            await using var command = dataSource.CreateCommand(sql);
            command.Parameters.AddWithValue("id", NpgsqlDbType.Uuid, item.Id);
            command.Parameters.AddWithValue("attempt_count", NpgsqlDbType.Integer, attemptCount);
            command.Parameters.AddWithValue("last_error", NpgsqlDbType.Varchar, error);
            command.Parameters.AddWithValue("dead_lettered_at", NpgsqlDbType.TimestampTz, timeProvider.GetUtcNow());
            await command.ExecuteNonQueryAsync(ct);
        }, cancellationToken);

        OrdersTelemetry.RecordOutboxDeadLettered(item.Topic);
    }

    private static void AddHeader(Headers headers, string name, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value))
        {
            headers.Add(name, Encoding.UTF8.GetBytes(value));
        }
    }

    private sealed record PendingSagaCommand(
        Guid Id,
        Guid OrderId,
        string Topic,
        string Key,
        string Payload,
        string CorrelationId,
        string? TraceParent,
        string? TraceState,
        int AttemptCount);
}

internal static partial class SagaOutboxLog
{
    [LoggerMessage(EventId = 6020, Level = LogLevel.Information, Message = "Published saga outbox command {CommandId} for order {OrderId} to {Topic}")]
    public static partial void Published(ILogger logger, Guid commandId, Guid orderId, string topic);

    [LoggerMessage(EventId = 6021, Level = LogLevel.Warning, Message = "Saga outbox command {CommandId} for order {OrderId} failed on attempt {AttemptCount}")]
    public static partial void RetryScheduled(ILogger logger, Guid commandId, Guid orderId, int attemptCount, Exception exception);

    [LoggerMessage(EventId = 6022, Level = LogLevel.Error, Message = "Saga outbox polling failed")]
    public static partial void BatchFailed(ILogger logger, Exception exception);

    [LoggerMessage(EventId = 6023, Level = LogLevel.Error, Message = "Saga outbox command {CommandId} for order {OrderId} moved to saga_outbox_dead_letters after {AttemptCount} attempts")]
    public static partial void DeadLettered(ILogger logger, Guid commandId, Guid orderId, int attemptCount, Exception exception);
}
