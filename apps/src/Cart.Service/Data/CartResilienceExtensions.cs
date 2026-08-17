using Microsoft.Extensions.DependencyInjection;
using Polly;
using Polly.CircuitBreaker;
using Polly.Retry;

namespace Cart.Service.Data;

public static class CartResilienceExtensions
{
    /// <summary>Registers a dedicated resilience pipeline for the cart store, separate from the shared Redis one.</summary>
    public static IServiceCollection AddCartRedisResilience(this IServiceCollection services)
    {
        return services.AddResiliencePipeline(CartStore.ResiliencePipelineName, builder =>
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
    }
}
