using System.Text.Json;
using BuildingBlocks;
using Microsoft.Extensions.Options;
using Orders.Application.Ports;
using Polly;
using Polly.Registry;
using StackExchange.Redis;

namespace Orders.Infrastructure.Caching;

public sealed class RedisOrderCache(
    IConnectionMultiplexer connectionMultiplexer,
    ResiliencePipelineProvider<string> pipelineProvider,
    IOptions<CacheOptions> options) : IOrderCache
{
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);
    private static readonly RedisValue LockToken = Environment.MachineName;
    private readonly CacheOptions _options = options.Value;
    private readonly ResiliencePipeline _pipeline = pipelineProvider.GetPipeline(ResilienceExtensions.RedisPipeline);

    public async Task<CacheLookup> GetOrCreateAsync(
        Guid id,
        Func<CancellationToken, Task<CachedOrder?>> factory,
        CancellationToken cancellationToken)
    {
        var database = connectionMultiplexer.GetDatabase();
        var cacheKey = OrderCacheKeys.Key(id);

        CachedOrder? cached;
        try
        {
            cached = await _pipeline.ExecuteAsync(
                async ct => await TryReadAsync(database, cacheKey).WaitAsync(ct),
                cancellationToken);
        }
        catch (Exception exception) when (ResilienceExtensions.IsInfrastructureFault(exception))
        {
            // Redis is unavailable: degrade gracefully to an uncached read rather than failing the request.
            OrdersTelemetry.RecordCacheBypass();
            var bypassedOrder = await factory(cancellationToken);
            return new CacheLookup(bypassedOrder, CacheLookupResult.Bypassed);
        }

        if (cached is not null)
        {
            OrdersTelemetry.RecordCacheHit();
            return new CacheLookup(cached, CacheLookupResult.Hit);
        }

        var lockKey = OrderCacheKeys.LockKey(id);
        var lockTimeout = TimeSpan.FromMilliseconds(_options.LockTimeoutMilliseconds);
        var acquiredLock = await database.LockTakeAsync(lockKey, LockToken, lockTimeout);

        // Milestone 48: drawn once per acquisition, independent of the lock itself - see RedisFencedWrite for why the lock's own timeout isn't enough.
        var fenceToken = acquiredLock ? await database.NextFenceTokenAsync(OrderCacheKeys.FenceSequenceKey(id)) : 0;

        if (!acquiredLock)
        {
            for (var attempt = 0; attempt < _options.LockRetryAttempts; attempt++)
            {
                await Task.Delay(_options.LockRetryDelayMilliseconds, cancellationToken);
                cached = await TryReadAsync(database, cacheKey);
                if (cached is not null)
                {
                    OrdersTelemetry.RecordCacheHit();
                    return new CacheLookup(cached, CacheLookupResult.Hit);
                }
            }

            OrdersTelemetry.RecordCacheMiss();
            var uncached = await factory(cancellationToken);
            return new CacheLookup(uncached, CacheLookupResult.Miss);
        }

        try
        {
            var value = await factory(cancellationToken);
            if (value is not null)
            {
                var payload = JsonSerializer.Serialize(value, SerializerOptions);
                var applied = await database.FencedSetAsync(
                    cacheKey, fenceToken, payload, TimeSpan.FromSeconds(_options.TimeToLiveSeconds));
                if (!applied)
                {
                    // This holder's lock expired and was re-acquired with a newer token before this write landed - discard, don't overwrite.
                    OrdersTelemetry.RecordFencedWriteRejected("order-cache");
                }
            }

            OrdersTelemetry.RecordCacheMiss();
            return new CacheLookup(value, CacheLookupResult.Miss);
        }
        finally
        {
            await database.LockReleaseAsync(lockKey, LockToken);
        }
    }

    private static async Task<CachedOrder?> TryReadAsync(IDatabase database, string cacheKey)
    {
        var value = await database.StringGetAsync(cacheKey);
        if (!value.HasValue)
        {
            return null;
        }

        return JsonSerializer.Deserialize<CachedOrder>((string)value!, SerializerOptions);
    }
    /// <summary>
    /// Deletes the cached value only, deliberately not the fence sequence -
    /// deleting it would restart the counter at 1, letting a paused writer
    /// still holding an old token reject every subsequent refill as
    /// "older" until the TTL expires, the exact hazard fencing prevents.
    /// </summary>
    public async Task InvalidateAsync(Guid id, CancellationToken cancellationToken)
    {
        var database = connectionMultiplexer.GetDatabase();
        await database.KeyDeleteAsync(OrderCacheKeys.Key(id));
    }

}
