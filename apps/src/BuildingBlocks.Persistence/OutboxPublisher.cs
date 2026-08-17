using System.Diagnostics;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace BuildingBlocks;

/// <summary>DbContext-owning half of the DbSet&lt;OutboxMessage&gt; every outbox-backed service needs.</summary>
public interface IOutboxDbContext
{
    DbSet<OutboxMessage> OutboxMessages { get; }
}

/// <summary>The service-specific half of "poll the outbox and publish": deserializes a row's payload and carries it to Kafka.</summary>
public interface IOutboxEventDispatcher
{
    Task<IReadOnlyDictionary<string, object?>> PublishAsync(OutboxMessage message, CancellationToken cancellationToken);
}

public sealed class OutboxPublisher<TDbContext>(
    IServiceScopeFactory scopeFactory,
    IOptions<OutboxOptions> options,
    IConfiguration configuration,
    ILogger<OutboxPublisher<TDbContext>> logger) : BackgroundService
    where TDbContext : DbContext, IOutboxDbContext
{
    private readonly OutboxOptions _options = options.Value;
    private readonly string _instanceId = configuration["InstanceId"] ?? Environment.MachineName;
    private DateTimeOffset _nextPendingSampleAt = DateTimeOffset.MinValue;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        OutboxPublisherLog.Started(logger, _instanceId);

        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                var shouldDelay = false;

                try
                {
                    shouldDelay = await ProcessBatchAsync(stoppingToken) == 0;
                }
                catch (Exception exception) when (exception is not OperationCanceledException)
                {
                    OutboxPublisherLog.LoopFailed(logger, _instanceId, exception);
                    shouldDelay = true;
                }

                if (shouldDelay)
                {
                    await Task.Delay(_options.PollIntervalMilliseconds, stoppingToken);
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
        finally
        {
            OutboxPublisherLog.Stopping(logger, _instanceId);
        }
    }

    /// <summary>Guarantees at-least-once delivery and, within one batch, publishes in occurred_at order, but not across retries.</summary>
    public async Task<int> ProcessBatchAsync(CancellationToken cancellationToken)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<TDbContext>();
        var dispatcher = scope.ServiceProvider.GetRequiredService<IOutboxEventDispatcher>();

        var messages = await ClaimBatchAsync(dbContext, cancellationToken);

        var attempts = await PublishAllAsync(messages, dispatcher, cancellationToken);
        foreach (var (message, attempt) in attempts)
        {
            await ApplyPublishAttemptAsync(dbContext, message, attempt, cancellationToken);
        }

        if (messages.Count > 0)
        {
            await dbContext.SaveChangesAsync(cancellationToken);
        }

        await SamplePendingIfDueAsync(dbContext, cancellationToken);

        return messages.Count;
    }

    /// <summary>Claims a batch by pushing each row's NextAttemptAt forward, inside a transaction that commits immediately.</summary>
    private async Task<List<OutboxMessage>> ClaimBatchAsync(TDbContext dbContext, CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;

        await using var transaction = await dbContext.Database.BeginTransactionAsync(cancellationToken);
        var messages = await dbContext.OutboxMessages
            .FromSqlInterpolated($"""
                SELECT *
                FROM outbox_messages
                WHERE processed_at IS NULL
                  AND next_attempt_at <= {now}
                ORDER BY occurred_at
                LIMIT {_options.BatchSize}
                FOR UPDATE SKIP LOCKED
                """)
            .ToListAsync(cancellationToken);

        if (messages.Count > 0)
        {
            var claimedUntil = now.AddSeconds(_options.ClaimWindowSeconds);
            foreach (var message in messages)
            {
                message.MarkClaimed(claimedUntil);
            }

            await dbContext.SaveChangesAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
        return messages;
    }

    /// <summary>Sampled, not run on every batch; the gauge only needs to be roughly current.</summary>
    private async Task SamplePendingIfDueAsync(TDbContext dbContext, CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;
        if (now < _nextPendingSampleAt)
        {
            return;
        }

        _nextPendingSampleAt = now.AddSeconds(_options.PendingSampleIntervalSeconds);

        var pending = await dbContext.OutboxMessages.CountAsync(message => message.ProcessedAt == null, cancellationToken);
        OrdersTelemetry.RecordOutboxPending(pending);
    }

    /// <summary>Phase one: attempts every message's Kafka publish concurrently, bounded by Outbox:MaxConcurrentPublishes.</summary>
    private async Task<List<(OutboxMessage Message, PublishAttempt Attempt)>> PublishAllAsync(
        List<OutboxMessage> messages,
        IOutboxEventDispatcher dispatcher,
        CancellationToken cancellationToken)
    {
        using var throttle = new SemaphoreSlim(Math.Max(1, _options.MaxConcurrentPublishes));

        var tasks = messages.Select(async message =>
        {
            await throttle.WaitAsync(cancellationToken);
            try
            {
                return (message, await TryPublishAsync(message, dispatcher, cancellationToken));
            }
            finally
            {
                throttle.Release();
            }
        });

        return (await Task.WhenAll(tasks)).ToList();
    }

    private static async Task<PublishAttempt> TryPublishAsync(
        OutboxMessage message, IOutboxEventDispatcher dispatcher, CancellationToken cancellationToken)
    {
        using var activity = CreateActivity(message);

        try
        {
            var context = await dispatcher.PublishAsync(message, cancellationToken);
            return PublishAttempt.Success(context, activity?.TraceId.ToString() ?? string.Empty);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return PublishAttempt.Failure(exception);
        }
    }

    /// <summary>Phase two: applies one message's already-settled publish outcome to this method's own DbContext, sequentially.</summary>
    private async Task ApplyPublishAttemptAsync(
        TDbContext dbContext, OutboxMessage message, PublishAttempt attempt, CancellationToken cancellationToken)
    {
        if (attempt.Succeeded)
        {
            var scopeState = new Dictionary<string, object?>(attempt.Context!)
            {
                ["CorrelationId"] = message.CorrelationId,
                ["EventId"] = message.Id,
                ["TraceId"] = attempt.TraceId ?? string.Empty
            };
            using var logScope = logger.BeginScope(scopeState);

            message.MarkPublished(DateTimeOffset.UtcNow);
            OrdersTelemetry.RecordOutboxPublished(message.EventType);
            OutboxPublisherLog.Published(logger, message.Id, _instanceId);
            return;
        }

        message.MarkFailed(DateTimeOffset.UtcNow, attempt.Exception!.Message, _options.MaximumRetryDelaySeconds);
        OrdersTelemetry.RecordOutboxRetry(message.EventType);

        if (message.AttemptCount >= _options.MaximumAttempts)
        {
            await DeadLetterAsync(dbContext, message, cancellationToken);
            OrdersTelemetry.RecordOutboxDeadLettered(message.EventType);
            OutboxPublisherLog.DeadLettered(logger, message.Id, message.AttemptCount, _instanceId, attempt.Exception);
            return;
        }

        OutboxPublisherLog.RetryScheduled(
            logger,
            message.Id,
            message.AttemptCount,
            message.NextAttemptAt,
            _instanceId,
            attempt.Exception);
    }

    private readonly record struct PublishAttempt(
        bool Succeeded,
        IReadOnlyDictionary<string, object?>? Context,
        string? TraceId,
        Exception? Exception)
    {
        public static PublishAttempt Success(IReadOnlyDictionary<string, object?> context, string traceId) =>
            new(true, context, traceId, null);

        public static PublishAttempt Failure(Exception exception) =>
            new(false, null, null, exception);
    }

    /// <summary>Moves a row that exhausted OutboxOptions.MaximumAttempts out of the pending set for good.</summary>
    private static async Task DeadLetterAsync(TDbContext dbContext, OutboxMessage message, CancellationToken cancellationToken)
    {
        await dbContext.Database.ExecuteSqlInterpolatedAsync(
            $"""
            INSERT INTO outbox_dead_letters
                (id, event_type, payload, occurred_at, correlation_id, trace_parent, trace_state, attempt_count, last_error, dead_lettered_at)
            VALUES
                ({message.Id}, {message.EventType}, {message.Payload}, {message.OccurredAt}, {message.CorrelationId},
                 {message.TraceParent}, {message.TraceState}, {message.AttemptCount}, {message.LastError}, {DateTimeOffset.UtcNow})
            ON CONFLICT (id) DO NOTHING
            """,
            cancellationToken);

        dbContext.OutboxMessages.Remove(message);
    }

    private static Activity? CreateActivity(OutboxMessage message)
    {
        var activity = OrdersTelemetry.StartActivity(
            "outbox.publish",
            ActivityKind.Producer,
            message.TraceParent,
            message.TraceState);
        activity?.SetTag("messaging.system", "kafka");
        activity?.SetTag("messaging.operation.type", "publish");
        activity?.SetTag("messaging.message.id", message.Id);
        activity?.SetTag("correlation.id", message.CorrelationId);
        return activity;
    }
}

public sealed partial class OutboxPublisherLog
{
    [LoggerMessage(EventId = 3000, Level = LogLevel.Information, Message = "Outbox publisher started on instance {InstanceId}")]
    public static partial void Started(ILogger logger, string instanceId);

    [LoggerMessage(EventId = 3001, Level = LogLevel.Information, Message = "Published outbox event {EventId} on instance {InstanceId}")]
    public static partial void Published(ILogger logger, Guid eventId, string instanceId);

    [LoggerMessage(EventId = 3002, Level = LogLevel.Warning, Message = "Outbox event {EventId} failed on attempt {AttemptCount}; retry at {NextAttemptAt} on instance {InstanceId}")]
    public static partial void RetryScheduled(ILogger logger, Guid eventId, int attemptCount, DateTimeOffset nextAttemptAt, string instanceId, Exception exception);

    [LoggerMessage(EventId = 3003, Level = LogLevel.Error, Message = "Outbox polling failed on instance {InstanceId}")]
    public static partial void LoopFailed(ILogger logger, string instanceId, Exception exception);

    [LoggerMessage(EventId = 3004, Level = LogLevel.Information, Message = "Outbox publisher is stopping gracefully on instance {InstanceId}")]
    public static partial void Stopping(ILogger logger, string instanceId);

    [LoggerMessage(EventId = 3005, Level = LogLevel.Error, Message = "Outbox event {EventId} moved to outbox_dead_letters after {AttemptCount} attempts on instance {InstanceId}")]
    public static partial void DeadLettered(ILogger logger, Guid eventId, int attemptCount, string instanceId, Exception exception);
}
