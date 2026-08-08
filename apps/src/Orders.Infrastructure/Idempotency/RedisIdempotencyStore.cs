using System.Text.Json;
using BuildingBlocks;
using Microsoft.Extensions.Options;
using Orders.Application.Ports;
using Polly;
using Polly.Registry;
using StackExchange.Redis;

namespace Orders.Infrastructure.Idempotency;

public sealed class RedisIdempotencyStore(
    IConnectionMultiplexer connectionMultiplexer,
    ResiliencePipelineProvider<string> pipelineProvider,
    IOptions<IdempotencyOptions> options) : IIdempotencyStore
{
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);
    private static readonly RedisValue LockToken = Environment.MachineName;
    private readonly IdempotencyOptions _options = options.Value;
    private readonly ResiliencePipeline _pipeline = pipelineProvider.GetPipeline(ResilienceExtensions.RedisPipeline);

    public async Task<IdempotencyLookup> GetOrCreateAsync(
        string idempotencyKey,
        Func<CancellationToken, Task<CachedOrder>> factory,
        CancellationToken cancellationToken)
    {
        var database = connectionMultiplexer.GetDatabase();
        var cacheKey = IdempotencyKeys.Key(idempotencyKey);

        CachedOrder? existing;
        try
        {
            existing = await _pipeline.ExecuteAsync(
                async ct => await TryReadAsync(database, cacheKey).WaitAsync(ct),
                cancellationToken);
        }
        catch (Exception exception) when (ResilienceExtensions.IsInfrastructureFault(exception))
        {
            // Redis is unavailable: the idempotency guarantee is a defense-in-depth safety net,
            // not the sole consistency mechanism - degrade to creating the order rather than
            // failing the request outright. A retried request during a Redis outage can
            // theoretically create a duplicate order; availability is favored over that guarantee here.
            OrdersTelemetry.RecordIdempotencyBypass();
            var bypassedOrder = await factory(cancellationToken);
            return new IdempotencyLookup(bypassedOrder, WasReplayed: false);
        }

        if (existing is not null)
        {
            OrdersTelemetry.RecordIdempotentReplay();
            return new IdempotencyLookup(existing, WasReplayed: true);
        }

        var lockKey = IdempotencyKeys.LockKey(idempotencyKey);
        var lockTimeout = TimeSpan.FromMilliseconds(_options.LockTimeoutMilliseconds);
        var acquiredLock = await database.LockTakeAsync(lockKey, LockToken, lockTimeout);

        // Drawn once per acquisition, strictly increasing,
        // independent of the lock itself - see RedisFencedWrite for why the
        // lock's own timeout can't be trusted to prevent a stale write.
        var fenceToken = acquiredLock
            ? await database.NextFenceTokenAsync(IdempotencyKeys.FenceSequenceKey(idempotencyKey))
            : 0;

        if (!acquiredLock)
        {
            for (var attempt = 0; attempt < _options.LockRetryAttempts; attempt++)
            {
                await Task.Delay(_options.LockRetryDelayMilliseconds, cancellationToken);
                existing = await TryReadAsync(database, cacheKey);
                if (existing is not null)
                {
                    OrdersTelemetry.RecordIdempotentReplay();
                    return new IdempotencyLookup(existing, WasReplayed: true);
                }
            }

            // The concurrent holder of the lock never finished in time - proceed rather than
            // block indefinitely; this is the same graceful-degradation tradeoff as above.
            OrdersTelemetry.RecordIdempotencyBypass();
            var uncoordinatedOrder = await factory(cancellationToken);
            return new IdempotencyLookup(uncoordinatedOrder, WasReplayed: false);
        }

        try
        {
            var created = await factory(cancellationToken);
            var payload = JsonSerializer.Serialize(created, SerializerOptions);
            var applied = await database.FencedSetAsync(
                cacheKey, fenceToken, payload, TimeSpan.FromHours(_options.TimeToLiveHours));
            if (!applied)
            {
                // This holder's lock had already expired and been
                // re-acquired by someone with a newer token by the time
                // this write landed - discarding it, not overwriting
                // whatever the newer holder already wrote.
                OrdersTelemetry.RecordFencedWriteRejected("idempotency");
            }

            return new IdempotencyLookup(created, WasReplayed: false);
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
}
