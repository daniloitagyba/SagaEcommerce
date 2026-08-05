using Microsoft.Extensions.DependencyInjection;
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

    public static IServiceCollection AddOrdersResilience(this IServiceCollection services)
    {
        services.AddResiliencePipeline(PostgresPipeline, builder =>
        {
            builder
                .AddRetry(new RetryStrategyOptions
                {
                    MaxRetryAttempts = 2,
                    Delay = TimeSpan.FromMilliseconds(100),
                    BackoffType = DelayBackoffType.Exponential
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
        return exception is BrokenCircuitException or TimeoutRejectedException;
    }
}
