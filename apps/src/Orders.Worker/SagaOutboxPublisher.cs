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
    private readonly ResiliencePipeline _pipeline = pipelineProvider.GetPipeline(ResilienceExtensions.KafkaProducerPipeline);

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
        var now = timeProvider.GetUtcNow();
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        var messages = new List<PendingSagaCommand>();

        const string selectSql = """
            SELECT id, order_id, topic, message_key, payload::text, correlation_id,
                   trace_parent, trace_state, attempt_count
            FROM saga_outbox_messages
            WHERE processed_at IS NULL AND next_attempt_at <= @now
            ORDER BY occurred_at
            LIMIT @batch_size
            FOR UPDATE SKIP LOCKED;
            """;

        await using (var command = new NpgsqlCommand(selectSql, connection, transaction))
        {
            command.Parameters.AddWithValue("now", NpgsqlDbType.TimestampTz, now);
            command.Parameters.AddWithValue("batch_size", NpgsqlDbType.Integer, _options.OutboxBatchSize);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                messages.Add(new PendingSagaCommand(
                    reader.GetGuid(0),
                    reader.GetGuid(1),
                    reader.GetString(2),
                    reader.GetString(3),
                    reader.GetString(4),
                    reader.GetString(5),
                    await reader.IsDBNullAsync(6, cancellationToken) ? null : reader.GetString(6),
                    await reader.IsDBNullAsync(7, cancellationToken) ? null : reader.GetString(7),
                    reader.GetInt32(8)));
            }
        }

        foreach (var item in messages)
        {
            await PublishAsync(connection, transaction, item, now, cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
        return messages.Count;
    }

    private async Task PublishAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        PendingSagaCommand item,
        DateTimeOffset now,
        CancellationToken cancellationToken)
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

            await _pipeline.ExecuteAsync(
                async ct => await producer.ProduceAsync(item.Topic, message, ct).WaitAsync(ct),
                cancellationToken);

            await using var mark = new NpgsqlCommand(
                "UPDATE saga_outbox_messages SET processed_at = @now, last_error = NULL WHERE id = @id",
                connection,
                transaction);
            mark.Parameters.AddWithValue("now", NpgsqlDbType.TimestampTz, now);
            mark.Parameters.AddWithValue("id", NpgsqlDbType.Uuid, item.Id);
            await mark.ExecuteNonQueryAsync(cancellationToken);
            SagaOutboxLog.Published(logger, item.Id, item.OrderId, item.Topic);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            var attemptCount = item.AttemptCount + 1;
            var exponent = Math.Min(attemptCount - 1, 6);
            var delaySeconds = Math.Min(_options.OutboxMaximumRetryDelaySeconds, 1 << exponent);
            var error = exception.Message.Length <= 2_000 ? exception.Message : exception.Message[..2_000];

            await using var mark = new NpgsqlCommand(
                """
                UPDATE saga_outbox_messages
                SET attempt_count = @attempt_count, next_attempt_at = @next_attempt_at, last_error = @last_error
                WHERE id = @id
                """,
                connection,
                transaction);
            mark.Parameters.AddWithValue("attempt_count", NpgsqlDbType.Integer, attemptCount);
            mark.Parameters.AddWithValue("next_attempt_at", NpgsqlDbType.TimestampTz, now.AddSeconds(delaySeconds));
            mark.Parameters.AddWithValue("last_error", NpgsqlDbType.Varchar, error);
            mark.Parameters.AddWithValue("id", NpgsqlDbType.Uuid, item.Id);
            await mark.ExecuteNonQueryAsync(cancellationToken);
            SagaOutboxLog.RetryScheduled(logger, item.Id, item.OrderId, attemptCount, exception);
        }
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
}
