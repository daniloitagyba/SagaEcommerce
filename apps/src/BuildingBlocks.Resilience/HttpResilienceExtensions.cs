using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Http.Resilience;
using Polly;

namespace BuildingBlocks;

public static class HttpResilienceExtensions
{
    public static IHttpClientBuilder AddCriticalHttpResilience(this IHttpClientBuilder builder)
    {
        builder.ConfigureHttpClient(client => client.Timeout = Timeout.InfiniteTimeSpan);
        builder.AddStandardResilienceHandler(options =>
        {
            options.AttemptTimeout.Timeout = TimeSpan.FromSeconds(2);
            options.Retry.MaxRetryAttempts = 2;
            options.Retry.Delay = TimeSpan.FromMilliseconds(200);
            options.Retry.BackoffType = DelayBackoffType.Exponential;
            options.Retry.UseJitter = true;
            options.CircuitBreaker.FailureRatio = 0.5;
            options.CircuitBreaker.MinimumThroughput = 10;
            options.CircuitBreaker.SamplingDuration = TimeSpan.FromSeconds(30);
            options.TotalRequestTimeout.Timeout = TimeSpan.FromSeconds(6);
        });
        return builder;
    }

    public static IHttpClientBuilder AddBestEffortHttpResilience(this IHttpClientBuilder builder)
    {
        builder.ConfigureHttpClient(client => client.Timeout = Timeout.InfiniteTimeSpan);
        builder.AddStandardResilienceHandler(options =>
        {
            options.AttemptTimeout.Timeout = TimeSpan.FromSeconds(2);
            options.Retry.MaxRetryAttempts = 1;
            options.Retry.Delay = TimeSpan.FromMilliseconds(200);
            options.Retry.BackoffType = DelayBackoffType.Exponential;
            options.Retry.UseJitter = true;
            options.CircuitBreaker.FailureRatio = 0.5;
            options.CircuitBreaker.MinimumThroughput = 10;
            options.CircuitBreaker.SamplingDuration = TimeSpan.FromSeconds(30);
            options.TotalRequestTimeout.Timeout = TimeSpan.FromSeconds(4);
        });
        return builder;
    }
}
