using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace BuildingBlocks;

public static class OrdersTelemetry
{
    public const string SourceName = "SagaEcommerce.Orders";

    public static readonly ActivitySource ActivitySource = new(SourceName);

    private static readonly Meter Meter = new(SourceName);
    private static readonly Counter<long> CreatedCounter = Meter.CreateCounter<long>("orders.created");
    private static readonly Counter<long> ProcessedCounter = Meter.CreateCounter<long>("orders.processed");
    private static readonly Counter<long> OutboxPublishedCounter = Meter.CreateCounter<long>("outbox.messages.published");
    private static readonly Counter<long> OutboxRetryCounter = Meter.CreateCounter<long>("outbox.publish.retries");
    private static readonly Counter<long> OutboxDeadLetteredCounter = Meter.CreateCounter<long>("outbox.dead_lettered");
    private static readonly Gauge<long> OutboxPendingGauge = Meter.CreateGauge<long>("outbox.messages.pending");
    private static readonly Counter<long> InboxDuplicateCounter = Meter.CreateCounter<long>("inbox.messages.duplicates");
    private static readonly Counter<long> ProcessingRetryCounter = Meter.CreateCounter<long>("messaging.processing.retries");
    private static readonly Counter<long> DeadLetterCounter = Meter.CreateCounter<long>("messaging.dead_letters");
    private static readonly Counter<long> CacheHitCounter = Meter.CreateCounter<long>("orders.cache.hits");
    private static readonly Counter<long> CacheMissCounter = Meter.CreateCounter<long>("orders.cache.misses");
    private static readonly Counter<long> CacheBypassCounter = Meter.CreateCounter<long>("orders.cache.bypassed");
    private static readonly Counter<long> IdempotentReplayCounter = Meter.CreateCounter<long>("orders.idempotency.replayed");
    private static readonly Counter<long> FencedWriteRejectedCounter = Meter.CreateCounter<long>("orders.redis.fenced_write_rejected");
    private static readonly Counter<long> RateLimitedCounter = Meter.CreateCounter<long>("orders.rate_limited");
    private static readonly Counter<long> DistributedRateLimitedCounter = Meter.CreateCounter<long>("orders.rate_limited.distributed");
    private static readonly Counter<long> DistributedRateLimitBypassCounter = Meter.CreateCounter<long>("orders.rate_limit.distributed_bypassed");
    private static readonly Counter<long> PaymentDecidedCounter = Meter.CreateCounter<long>("payments.decided");
    private static readonly Histogram<double> ProjectionLagHistogram = Meter.CreateHistogram<double>("orders.projection.lag_ms");
    private static readonly Counter<long> AntiEntropyDivergenceCounter = Meter.CreateCounter<long>("anti_entropy.divergences");
    private static readonly Counter<long> SettlementReconciliationUnresolvedCounter = Meter.CreateCounter<long>("payments.settlement_reconciliation.unresolved");
    private static readonly Counter<long> OrphanedSagaReplyCounter = Meter.CreateCounter<long>("saga.orphaned_reply");

    public static Activity? StartActivity(
        string name,
        ActivityKind kind,
        string? traceParent = null,
        string? traceState = null)
    {
        if (!string.IsNullOrWhiteSpace(traceParent)
            && ActivityContext.TryParse(traceParent, traceState, true, out var parentContext))
        {
            return ActivitySource.StartActivity(name, kind, parentContext);
        }

        return ActivitySource.StartActivity(name, kind);
    }

    public static void RecordCreated(string currency)
    {
        CreatedCounter.Add(1, new KeyValuePair<string, object?>("currency", currency));
    }

    public static void RecordProcessed(string result)
    {
        ProcessedCounter.Add(1, new KeyValuePair<string, object?>("result", result));
    }

    public static void RecordOutboxPublished(string eventType)
    {
        OutboxPublishedCounter.Add(1, new KeyValuePair<string, object?>("event.type", eventType));
    }

    public static void RecordOutboxRetry(string eventType)
    {
        OutboxRetryCounter.Add(1, new KeyValuePair<string, object?>("event.type", eventType));
    }

    public static void RecordOutboxPending(long count)
    {
        OutboxPendingGauge.Record(count);
    }

    public static void RecordOutboxDeadLettered(string eventType)
    {
        OutboxDeadLetteredCounter.Add(1, new KeyValuePair<string, object?>("event.type", eventType));
    }

    public static void RecordInboxDuplicate(string consumerName)
    {
        InboxDuplicateCounter.Add(1, new KeyValuePair<string, object?>("consumer.name", consumerName));
    }

    public static void RecordProcessingRetry(string topic)
    {
        ProcessingRetryCounter.Add(1, new KeyValuePair<string, object?>("messaging.destination.name", topic));
    }

    public static void RecordDeadLetter(string topic)
    {
        DeadLetterCounter.Add(1, new KeyValuePair<string, object?>("messaging.destination.name", topic));
    }

    public static void RecordCacheHit()
    {
        CacheHitCounter.Add(1);
    }

    /// <summary>Tagged by which invariant failed.</summary>
    public static void RecordAntiEntropyDivergence(string checkName)
    {
        AntiEntropyDivergenceCounter.Add(1, new KeyValuePair<string, object?>("check", checkName));
    }

    /// <summary>Tagged by the transition result (NotApplicable/IllegalTransition).</summary>
    public static void RecordSettlementReconciliationUnresolved(string transitionResult)
    {
        SettlementReconciliationUnresolvedCounter.Add(1, new KeyValuePair<string, object?>("transition_result", transitionResult));
    }

    /// <summary>Tagged by reply kind and whether the reply said the operation succeeded.</summary>
    public static void RecordOrphanedSagaReply(string replyKind, bool succeeded)
    {
        OrphanedSagaReplyCounter.Add(
            1,
            new KeyValuePair<string, object?>("reply_kind", replyKind),
            new KeyValuePair<string, object?>("succeeded", succeeded));
    }

    public static void RecordCacheMiss()
    {
        CacheMissCounter.Add(1);
    }

    public static void RecordCacheBypass()
    {
        CacheBypassCounter.Add(1);
    }

    public static void RecordIdempotentReplay()
    {
        IdempotentReplayCounter.Add(1);
    }

    /// <summary>A lock holder's write lost the fencing-token race.</summary>
    public static void RecordFencedWriteRejected(string store)
    {
        FencedWriteRejectedCounter.Add(1, new KeyValuePair<string, object?>("store", store));
    }

    public static void RecordRateLimited()
    {
        RateLimitedCounter.Add(1);
    }

    public static void RecordDistributedRateLimited()
    {
        DistributedRateLimitedCounter.Add(1);
    }

    public static void RecordDistributedRateLimitBypass()
    {
        DistributedRateLimitBypassCounter.Add(1);
    }

    public static void RecordPaymentDecided(bool approved)
    {
        PaymentDecidedCounter.Add(1, new KeyValuePair<string, object?>("approved", approved));
    }

    public static void RecordProjectionLag(string eventType, TimeSpan lag)
    {
        ProjectionLagHistogram.Record(lag.TotalMilliseconds, new KeyValuePair<string, object?>("event.type", eventType));
    }
}
