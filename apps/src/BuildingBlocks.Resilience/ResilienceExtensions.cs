using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Polly;
using Polly.CircuitBreaker;
using Polly.Retry;
using Polly.Timeout;

namespace BuildingBlocks;

public static class ResilienceExtensions
{
    public const string PostgresPipeline = "postgres";
    public const string KafkaProducerPipeline = "kafka-producer";
    public const string RedisPipeline = "redis";

    /// <summary>
    /// For CAS-guarded, multi-statement transactions (open connection,
    /// begin, one or more writes, commit) whose retry unit is the whole
    /// lambda - SagaOrchestrationStore and OrderStatusStore, specifically.
    ///
    /// Deliberately zero retries, unlike PostgresPipeline: a transient
    /// fault after this codebase's Postgres commands have already reached
    /// the server (the commit succeeded, only its acknowledgement was
    /// lost) is indistinguishable, from here, from one where nothing
    /// landed. Retrying the whole lambda in that ambiguous case does not
    /// corrupt anything - every write in this codebase's transactional
    /// stores is guarded by a CAS or an idempotent upsert, so a retry that
    /// finds "no match" is a safe no-op, not a double-apply - but it does
    /// silently swallow whatever the *caller* was going to do with a
    /// genuinely successful result (log a completion, invalidate a cache
    /// entry), because the caller sees the same null/false a legitimate
    /// race would produce. See
    /// docs/roadmap-milestones-91-99.md, "a Polly retry wraps whole
    /// database transactions". Rather than build a writer-identity column
    /// to tell the two cases apart internally, this pipeline hands the
    /// retry back to the layer that already knows how to redeliver safely
    /// end-to-end - Kafka's own at-least-once redelivery (or the caller's
    /// own leader-election-guarded sweep loop) - by not masking a failure
    /// as a transparent, silent retry in the first place.
    /// </summary>
    public const string PostgresTransactionPipeline = "postgres-transaction";

    public static IServiceCollection AddOrdersResilience(this IServiceCollection services)
    {
        services.AddResiliencePipeline(PostgresPipeline, builder =>
        {
            builder
                .AddRetry(new RetryStrategyOptions
                {
                    MaxRetryAttempts = 2,
                    Delay = TimeSpan.FromMilliseconds(100),
                    BackoffType = DelayBackoffType.Exponential,
                    UseJitter = true,
                    ShouldHandle = new PredicateBuilder()
                        .Handle<NpgsqlException>(exception => exception.IsTransient)
                        .Handle<TimeoutException>()
                        .Handle<IOException>()
                        .Handle<TimeoutRejectedException>()
                })
                .AddCircuitBreaker(new CircuitBreakerStrategyOptions
                {
                    FailureRatio = 0.5,
                    MinimumThroughput = 4,
                    SamplingDuration = TimeSpan.FromSeconds(10),
                    BreakDuration = TimeSpan.FromSeconds(5)
                })
                .AddTimeout(TimeSpan.FromSeconds(2));
        });

        // Its own circuit breaker, not shared with PostgresPipeline - see
        // PostgresTransactionPipeline's own comment. A batch sweep (e.g.
        // ClaimTimedOutAndResolveAsync claiming and resolving up to 100
        // rows in one transaction) legitimately takes longer than a single
        // request-path query; before this it shared PostgresPipeline's
        // breaker, so a slow-but-healthy sweep could trip the same breaker
        // every ordinary request-path Postgres call depends on, including
        // the readiness probe. See
        // docs/roadmap-milestones-91-99.md, "one shared Postgres circuit
        // breaker per process".
        services.AddResiliencePipeline(PostgresTransactionPipeline, builder =>
        {
            builder
                .AddCircuitBreaker(new CircuitBreakerStrategyOptions
                {
                    FailureRatio = 0.5,
                    MinimumThroughput = 4,
                    SamplingDuration = TimeSpan.FromSeconds(10),
                    BreakDuration = TimeSpan.FromSeconds(5)
                })
                .AddTimeout(TimeSpan.FromSeconds(5));
        });

        services.AddResiliencePipeline(KafkaProducerPipeline, builder =>
        {
            builder
                .AddRetry(new RetryStrategyOptions
                {
                    MaxRetryAttempts = 1,
                    Delay = TimeSpan.FromMilliseconds(100),
                    BackoffType = DelayBackoffType.Constant
                })
                .AddCircuitBreaker(new CircuitBreakerStrategyOptions
                {
                    FailureRatio = 0.5,
                    MinimumThroughput = 4,
                    SamplingDuration = TimeSpan.FromSeconds(10),
                    BreakDuration = TimeSpan.FromSeconds(5)
                })
                .AddTimeout(TimeSpan.FromSeconds(3));
        });

        services.AddResiliencePipeline(RedisPipeline, builder =>
        {
            builder
                .AddCircuitBreaker(new CircuitBreakerStrategyOptions
                {
                    FailureRatio = 0.5,
                    MinimumThroughput = 4,
                    SamplingDuration = TimeSpan.FromSeconds(10),
                    BreakDuration = TimeSpan.FromSeconds(5)
                })
                .AddTimeout(TimeSpan.FromMilliseconds(150));
        });

        return services;
    }

    public static bool IsInfrastructureFault(Exception exception)
    {
        return exception is BrokenCircuitException
            or TimeoutRejectedException
            or TimeoutException
            or IOException
            || exception is NpgsqlException { IsTransient: true };
    }
}
