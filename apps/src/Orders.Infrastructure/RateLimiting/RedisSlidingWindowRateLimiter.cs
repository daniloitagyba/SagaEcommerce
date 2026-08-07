using BuildingBlocks;
using Microsoft.Extensions.Options;
using Polly;
using Polly.Registry;
using StackExchange.Redis;

namespace Orders.Infrastructure.RateLimiting;

public sealed record RateLimitDecision(bool Allowed, int Count, int Limit);

/// <summary>
/// Milestone 38: a cluster-wide sliding-window-log rate limiter, contrasted
/// with Milestone 11's per-pod in-memory token bucket - with 3 replicas,
/// M11's limiter effectively enforces (replica count * per-pod limit), not
/// the configured number. This shares state in Redis (a sorted set per key,
/// member = per-request GUID, score = timestamp) so the limit means what
/// it says. Sliding-window-log, not fixed-window, to avoid the up-to-2x
/// burst a fixed window allows at its boundary. Applied alongside, not
/// replacing, M11's limiter - a fast local check catches obvious abuse
/// without a Redis round-trip; this is the authoritative cap. Fails OPEN
/// on Redis unavailability, same as RedisOrderCache/RedisIdempotencyStore.
/// </summary>
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
