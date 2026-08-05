using System.Diagnostics;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace BuildingBlocks;

// DbContext-owning half of the DbSet<OutboxMessage> every outbox-backed
// service needs - just enough surface for OutboxPublisher<TDbContext> to
// poll and update rows without depending on the concrete DbContext type.
public interface IOutboxDbContext
{
    DbSet<OutboxMessage> OutboxMessages { get; }
}

// The one part of "poll the outbox and publish" that's genuinely
// service-specific: which event type(s) a row's EventType/Payload
// deserializes to, and which publisher interface carries it to Kafka.
// Implementations live in each service (they close over that service's own
// IOrderEventPublisher/IPaymentEventPublisher/IInventoryEventPublisher and
// event record types) - only the polling/transaction/retry/telemetry
// skeleton around this is shared.
public interface IOutboxEventDispatcher
{
    // The returned dictionary becomes structured logger scope state
    // (e.g. {"OrderId": ...} or {"ReservationId": ..., "Sku": ...}) so the
    // per-message correlation each service's original hand-rolled
    // publisher logged is preserved without the shared publisher needing
    // to know the shape of any specific event.
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
            // Normal cooperative shutdown.
        }
        finally
        {
            OutboxPublisherLog.Stopping(logger, _instanceId);
        }
    }

    private async Task<int> ProcessBatchAsync(CancellationToken cancellationToken)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<TDbContext>();
        var dispatcher = scope.ServiceProvider.GetRequiredService<IOutboxEventDispatcher>();
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

        foreach (var message in messages)
        {
            await PublishAsync(message, dispatcher, cancellationToken);
        }

        await dbContext.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return messages.Count;
    }

    private async Task PublishAsync(
        OutboxMessage message,
        IOutboxEventDispatcher dispatcher,
        CancellationToken cancellationToken)
    {
        using var activity = CreateActivity(message);

        try
        {
            var context = await dispatcher.PublishAsync(message, cancellationToken);

            var scopeState = new Dictionary<string, object?>(context)
            {
                ["CorrelationId"] = message.CorrelationId,
                ["EventId"] = message.Id,
                ["TraceId"] = activity?.TraceId.ToString() ?? string.Empty
            };
            using var logScope = logger.BeginScope(scopeState);

            message.MarkPublished(DateTimeOffset.UtcNow);
            OrdersTelemetry.RecordOutboxPublished(message.EventType);
            OutboxPublisherLog.Published(logger, message.Id, _instanceId);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            message.MarkFailed(DateTimeOffset.UtcNow, exception.Message, _options.MaximumRetryDelaySeconds);
            OrdersTelemetry.RecordOutboxRetry(message.EventType);
            OutboxPublisherLog.RetryScheduled(
                logger,
                message.Id,
                message.AttemptCount,
                message.NextAttemptAt,
                _instanceId,
                exception);
        }
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
}
