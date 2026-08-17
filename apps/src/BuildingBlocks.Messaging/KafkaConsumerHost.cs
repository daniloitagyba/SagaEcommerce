using System.Diagnostics;
using Confluent.Kafka;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace BuildingBlocks;

public sealed class KafkaConsumerHost<TValue>(
    string bootstrapServers,
    string consumerGroup,
    string clientId,
    IReadOnlyList<string> subscribeTopics,
    string deadLetterTopic,
    MessageProcessingOptions processingOptions,
    Func<ConsumeResult<string, TValue>, CancellationToken, Task> processAsync,
    Func<ConsumeResult<string, TValue>, Exception, int, CancellationToken, Task> publishDeadLetterAsync,
    ILogger logger) : BackgroundService
{
    private const int AutoCommitIntervalMilliseconds = 5_000;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var config = new ConsumerConfig
        {
            BootstrapServers = bootstrapServers,
            GroupId = consumerGroup,
            ClientId = clientId,
            AutoOffsetReset = AutoOffsetReset.Earliest,
            EnableAutoCommit = true,
            EnableAutoOffsetStore = false,
            AutoCommitIntervalMs = AutoCommitIntervalMilliseconds,
            AllowAutoCreateTopics = false,
            SessionTimeoutMs = 10_000
        };

        using var consumer = new ConsumerBuilder<string, TValue>(config)
            .SetPartitionsAssignedHandler((_, partitions) =>
                KafkaConsumerHostLog.PartitionsAssigned(logger, partitions.Count, consumerGroup))
            .SetPartitionsRevokedHandler((_, partitions) =>
                KafkaConsumerHostLog.PartitionsRevoked(logger, partitions.Count, consumerGroup))
            .Build();
        consumer.Subscribe(subscribeTopics);
#pragma warning disable CA1873
        KafkaConsumerHostLog.Started(logger, string.Join(", ", subscribeTopics), consumerGroup);
#pragma warning restore CA1873

        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                ConsumeResult<string, TValue> consumeResult;
                try
                {
                    consumeResult = consumer.Consume(stoppingToken);
                }
                catch (ConsumeException exception)
                {
                    KafkaConsumerHostLog.ConsumeFailed(logger, exception.Error.Reason, exception);
                    await DelayForInfrastructureAsync(stoppingToken);
                    continue;
                }

                var shouldCommit = await ProcessWithRetriesAsync(consumeResult, stoppingToken);
                if (!shouldCommit)
                {
                    consumer.Seek(consumeResult.TopicPartitionOffset);
                    continue;
                }

                try
                {
                    consumer.StoreOffset(consumeResult);
                }
                catch (KafkaException exception)
                {
                    KafkaConsumerHostLog.CommitFailed(logger, consumeResult.TopicPartitionOffset.ToString(), exception);
                    await DelayForInfrastructureAsync(stoppingToken);
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            KafkaConsumerHostLog.Stopping(logger);
        }
        finally
        {
            consumer.Close();
        }
    }

    private async Task<bool> ProcessWithRetriesAsync(
        ConsumeResult<string, TValue> consumeResult,
        CancellationToken cancellationToken)
    {
        for (var attempt = 1; attempt <= processingOptions.MaximumAttempts; attempt++)
        {
            try
            {
                await processAsync(consumeResult, cancellationToken);
                return true;
            }
            catch (Exception exception) when (exception is NpgsqlException || ResilienceExtensions.IsInfrastructureFault(exception))
            {
                KafkaConsumerHostLog.InfrastructureFailure(logger, consumeResult.TopicPartitionOffset.ToString(), exception);
                await DelayForInfrastructureAsync(cancellationToken);
                return false;
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                if (attempt < processingOptions.MaximumAttempts)
                {
                    var delay = RetryDelayCalculator.Calculate(
                        attempt,
                        processingOptions.InitialRetryDelayMilliseconds,
                        processingOptions.MaximumRetryDelayMilliseconds);
                    OrdersTelemetry.RecordProcessingRetry(consumeResult.Topic);
                    KafkaConsumerHostLog.RetryScheduled(
                        logger,
                        consumeResult.TopicPartitionOffset.ToString(),
                        attempt,
                        delay.TotalMilliseconds,
                        exception);
                    await Task.Delay(delay, cancellationToken);
                    continue;
                }

                return await TryDeadLetterAsync(consumeResult, exception, attempt, cancellationToken);
            }
        }

        throw new UnreachableException();
    }

    private async Task<bool> TryDeadLetterAsync(
        ConsumeResult<string, TValue> consumeResult,
        Exception exception,
        int attemptCount,
        CancellationToken cancellationToken)
    {
        try
        {
            await publishDeadLetterAsync(consumeResult, exception, attemptCount, cancellationToken);
            OrdersTelemetry.RecordProcessed("dead-letter");
            OrdersTelemetry.RecordDeadLetter(deadLetterTopic);
            KafkaConsumerHostLog.DeadLettered(
                logger,
                consumeResult.TopicPartitionOffset.ToString(),
                deadLetterTopic,
                attemptCount);
            return true;
        }
        catch (Exception deadLetterException) when (deadLetterException is not OperationCanceledException)
        {
            KafkaConsumerHostLog.DeadLetterFailed(logger, consumeResult.TopicPartitionOffset.ToString(), deadLetterException);
            await DelayForInfrastructureAsync(cancellationToken);
            return false;
        }
    }

    private Task DelayForInfrastructureAsync(CancellationToken cancellationToken)
    {
        return Task.Delay(processingOptions.InfrastructureRetryDelayMilliseconds, cancellationToken);
    }
}

public sealed partial class KafkaConsumerHostLog
{
    [LoggerMessage(EventId = 2500, Level = LogLevel.Information, Message = "Consumer subscribed to topic {Topic} with consumer group {GroupId}")]
    public static partial void Started(ILogger logger, string topic, string groupId);

    [LoggerMessage(EventId = 2501, Level = LogLevel.Information, Message = "Consumer is stopping gracefully")]
    public static partial void Stopping(ILogger logger);

    [LoggerMessage(EventId = 2502, Level = LogLevel.Error, Message = "Kafka consume failed: {Reason}")]
    public static partial void ConsumeFailed(ILogger logger, string reason, Exception exception);

    [LoggerMessage(EventId = 2503, Level = LogLevel.Warning, Message = "Processing at {Offset} failed on attempt {Attempt}; retrying in {DelayMilliseconds} ms")]
    public static partial void RetryScheduled(ILogger logger, string offset, int attempt, double delayMilliseconds, Exception exception);

    [LoggerMessage(EventId = 2504, Level = LogLevel.Error, Message = "Moved message at {Offset} to {DeadLetterTopic} after {AttemptCount} attempts")]
    public static partial void DeadLettered(ILogger logger, string offset, string deadLetterTopic, int attemptCount);

    [LoggerMessage(EventId = 2505, Level = LogLevel.Error, Message = "Infrastructure failure while processing message at {Offset}; offset remains uncommitted")]
    public static partial void InfrastructureFailure(ILogger logger, string offset, Exception exception);

    [LoggerMessage(EventId = 2506, Level = LogLevel.Error, Message = "Failed to publish message at {Offset} to the dead-letter topic; offset remains uncommitted")]
    public static partial void DeadLetterFailed(ILogger logger, string offset, Exception exception);

    [LoggerMessage(EventId = 2507, Level = LogLevel.Warning, Message = "Failed to commit Kafka offset {Offset}; Inbox will prevent duplicate processing")]
    public static partial void CommitFailed(ILogger logger, string offset, Exception exception);

    [LoggerMessage(EventId = 2508, Level = LogLevel.Information, Message = "Partitions assigned: {PartitionCount} for consumer group {GroupId}")]
    public static partial void PartitionsAssigned(ILogger logger, int partitionCount, string groupId);

    [LoggerMessage(EventId = 2509, Level = LogLevel.Information, Message = "Partitions revoked: {PartitionCount} for consumer group {GroupId}")]
    public static partial void PartitionsRevoked(ILogger logger, int partitionCount, string groupId);
}
