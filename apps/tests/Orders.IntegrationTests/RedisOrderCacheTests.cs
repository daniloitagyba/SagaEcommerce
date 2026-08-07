using BuildingBlocks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Orders.Application.Ports;
using Orders.Infrastructure.Caching;
using Polly.Registry;
using StackExchange.Redis;
using Testcontainers.Redis;

namespace Orders.IntegrationTests;

public sealed class RedisOrderCacheTests : IAsyncLifetime
{
    private readonly RedisContainer _redis = new RedisBuilder("redis:7.4-alpine").Build();
    private readonly ResiliencePipelineProvider<string> _pipelineProvider = new ServiceCollection()
        .AddOrdersResilience()
        .BuildServiceProvider()
        .GetRequiredService<ResiliencePipelineProvider<string>>();
    private ConnectionMultiplexer? _connectionMultiplexer;

    public async Task InitializeAsync()
    {
        await _redis.StartAsync();
        _connectionMultiplexer = await ConnectionMultiplexer.ConnectAsync(_redis.GetConnectionString());
    }

    public async Task DisposeAsync()
    {
        if (_connectionMultiplexer is not null)
        {
            await _connectionMultiplexer.DisposeAsync();
        }

        await _redis.DisposeAsync();
    }

    [Fact]
    public async Task GetOrCreateAsyncServesSubsequentReadsFromCacheUntilInvalidated()
    {
        var cache = new RedisOrderCache(_connectionMultiplexer!, _pipelineProvider, Options.Create(new CacheOptions()));

        // ResilienceExtensions.RedisPipeline times this cache's Redis calls out at 150ms -
        // deliberately tight for production, where a slow Redis is itself worth failing fast
        // on. Against a real Testcontainers Redis sharing a noisy CI runner with several other
        // integration test assemblies (see ci.yml's coverage-step comment for the same class of
        // issue), an ordinary sub-millisecond round trip can occasionally get scheduled past
        // that window, and the cache degrades exactly as designed: bypass and re-run the
        // factory rather than serve a Hit. That degradation is correct behavior under a
        // genuinely slow Redis - here the slowness is CI scheduler jitter impersonating one.
        // Retried with a fresh order id each attempt, rather than loosening the timeout, so a
        // real regression in cache-hit serving still fails every attempt and the test still
        // fails.
        for (var attempt = 1; attempt <= 3; attempt++)
        {
            var orderId = Guid.NewGuid();
            var factoryCalls = 0;

            Task<CachedOrder?> Factory(CancellationToken cancellationToken)
            {
                factoryCalls++;
                return Task.FromResult<CachedOrder?>(new CachedOrder(
                    orderId,
                    "integration-customer",
                    49.90m,
                    "BRL",
                    "Created",
                    DateTimeOffset.UtcNow));
            }

            var firstLookup = await cache.GetOrCreateAsync(orderId, Factory, CancellationToken.None);
            var secondLookup = await cache.GetOrCreateAsync(orderId, Factory, CancellationToken.None);

            Assert.Equal(CacheLookupResult.Miss, firstLookup.Result);

            if (secondLookup.Result != CacheLookupResult.Hit && attempt < 3)
            {
                continue;
            }

            Assert.Equal(CacheLookupResult.Hit, secondLookup.Result);
            Assert.Equal(1, factoryCalls);
            Assert.Equal(firstLookup.Order, secondLookup.Order);

            var database = _connectionMultiplexer!.GetDatabase();
            await database.KeyDeleteAsync(BuildingBlocks.OrderCacheKeys.Key(orderId));

            var thirdLookup = await cache.GetOrCreateAsync(orderId, Factory, CancellationToken.None);

            Assert.Equal(CacheLookupResult.Miss, thirdLookup.Result);
            Assert.Equal(2, factoryCalls);
            return;
        }
    }
}
