using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Http.Resilience;
using Polly;

namespace BuildingBlocks;

// Every outbound HttpClient in this codebase called the bare
// .AddStandardResilienceHandler() with no configuration, paired with an
// HttpClient.Timeout of 3-5 seconds. Microsoft.Extensions.Http.Resilience's
// defaults include a ~2 second base retry delay with exponential backoff -
// HttpClient.Timeout wraps the *entire* pipeline, retries included, so a
// 3-5 second outer budget could never fit a second attempt. Retries were
// configured and never happened: the caller saw a bare
// TaskCanceledException from the outer timeout instead of the upstream's
// real error. These two helpers replace that shape with an explicit
// pipeline whose TotalRequestTimeout is the only timeout that matters -
// callers should not also set HttpClient.Timeout once using either of these.
public static class HttpResilienceExtensions
{
    // For a call the request cannot succeed without (checkout pricing
    // against Catalog, the storefront's own checkout forward to Orders).
    // Two retries is a real second attempt, not just padding.
    public static IHttpClientBuilder AddCriticalHttpResilience(this IHttpClientBuilder builder)
    {
        // HttpClient.Timeout wraps the entire pipeline below, so it must
        // not be left at whatever shorter value a caller set before
        // switching to this method - see this class's own header comment
        // for the bug that shape caused. HttpClientFactory's own default
        // (100 seconds, applied when nothing overrides it) is already
        // comfortably above TotalRequestTimeout below, so setting it to
        // Infinite here just removes any leftover override rather than
        // relying on every caller to remember to delete its own.
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

    // For a call whose failure degrades a feature rather than breaking one
    // (Orders.Worker's bestsellers lookup, the anti-entropy sweep's
    // cross-service reads, Storefront's browse-time catalog/cart/inventory
    // proxies). One retry, not two - worth a second attempt, not worth
    // holding the caller up chasing a third.
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
