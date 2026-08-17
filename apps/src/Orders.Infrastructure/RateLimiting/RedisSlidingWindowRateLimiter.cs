using BuildingBlocks;
using Microsoft.Extensions.Options;
using Polly;
using Polly.Registry;
using StackExchange.Redis;

namespace Orders.Infrastructure.RateLimiting;

public sealed record RateLimitDecision(bool Allowed, int Count, int Limit);

/// <summary>Cluster-wide sliding-window-log rate limiter backed by a Redis sorted set, applied alongside the per-pod in-memory token bucket as the authoritative cap.</summary>
public sealed class RedisSlidingWindowRateLimiter(
    IConnectionMultiplexer connectionMultiplexer,
    ResiliencePipelineProvider<string> pipelineProvider,
    IOptions<DistributedRateLimitOptions> options)
{
    private const string SlidingWindowScript = """
        local key = KEYS[1]
        local now = tonumber(ARGV[1])
        local window_ms = tonumber(ARGV[2])
        local limit = tonumber(ARGV[3])
        local member = ARGV[4]

        redis.call('ZREMRANGEBYSCORE', key, '-inf', now - window_ms)
        local count = redis.call('ZCARD', key)

        if count < limit then
            redis.call('ZADD', key, now, member)
            redis.call('PEXPIRE', key, window_ms)
            return count + 1
        end

        return -1 - count
        """;

    private readonly DistributedRateLimitOptions _options = options.Value;
    private readonly ResiliencePipeline _pipeline = pipelineProvider.GetPipeline(ResilienceExtensions.RedisPipeline);

    public async Task<RateLimitDecision> TryAcquireAsync(string key, CancellationToken cancellationToken)
    {
        try
        {
            return await _pipeline.ExecuteAsync(async ct =>
            {
                var database = connectionMultiplexer.GetDatabase();
                var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                var windowMilliseconds = (long)TimeSpan.FromSeconds(_options.WindowSeconds).TotalMilliseconds;

                var result = (long)await database.ScriptEvaluateAsync(
                    SlidingWindowScript,
                    [key],
                    [now, windowMilliseconds, _options.Limit, Guid.NewGuid().ToString("N")]).WaitAsync(ct);

                return result >= 0
                    ? new RateLimitDecision(Allowed: true, Count: (int)result, _options.Limit)
                    : new RateLimitDecision(Allowed: false, Count: (int)(-result - 1), _options.Limit);
            }, cancellationToken);
        }
        catch (Exception exception) when (ResilienceExtensions.IsInfrastructureFault(exception))
        {
            OrdersTelemetry.RecordDistributedRateLimitBypass();
            return new RateLimitDecision(Allowed: true, Count: -1, _options.Limit);
        }
    }
}
