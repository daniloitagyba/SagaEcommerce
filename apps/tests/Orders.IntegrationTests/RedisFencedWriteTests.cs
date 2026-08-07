using BuildingBlocks;
using StackExchange.Redis;
using Testcontainers.Redis;

namespace Orders.IntegrationTests;

public sealed class RedisFencedWriteTests : IAsyncLifetime
{
    private readonly RedisContainer _redis = new RedisBuilder("redis:7.4-alpine").Build();
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

    /// <summary>
    /// Reproduces the hazard fencing tokens exist for: holder A stalls past
    /// its lock timeout, B acquires the lock and writes first. Without a
    /// fencing token, A resuming would silently clobber B's newer value -
    /// exactly what happened before Milestone 48. A's write must be rejected.
    /// </summary>
    [Fact]
    public async Task StaleHolderWriteIsRejectedAfterNewerHolderAlreadyWrote()
    {
        var database = _connectionMultiplexer!.GetDatabase();
        var valueKey = $"test:fenced:{Guid.NewGuid()}";
        var sequenceKey = $"{valueKey}:seq";

        var tokenA = await database.NextFenceTokenAsync(sequenceKey); // holder A acquires first (token 1)
        var tokenB = await database.NextFenceTokenAsync(sequenceKey); // A stalls; B's lock timeout fires, B acquires (token 2)

        var bApplied = await database.FencedSetAsync(valueKey, tokenB, "B's fresh value", TimeSpan.FromMinutes(1));
        var aApplied = await database.FencedSetAsync(valueKey, tokenA, "A's stale value", TimeSpan.FromMinutes(1));

        Assert.True(bApplied);
        Assert.False(aApplied);

        var finalValue = await database.StringGetAsync(valueKey);
        Assert.Equal("B's fresh value", (string?)finalValue);
    }

    [Fact]
    public async Task WritesInAcquisitionOrderAllSucceed()
    {
        var database = _connectionMultiplexer!.GetDatabase();
        var valueKey = $"test:fenced:{Guid.NewGuid()}";
        var sequenceKey = $"{valueKey}:seq";

        var tokenA = await database.NextFenceTokenAsync(sequenceKey);
        var aApplied = await database.FencedSetAsync(valueKey, tokenA, "first", TimeSpan.FromMinutes(1));

        var tokenB = await database.NextFenceTokenAsync(sequenceKey);
        var bApplied = await database.FencedSetAsync(valueKey, tokenB, "second", TimeSpan.FromMinutes(1));

        Assert.True(aApplied);
        Assert.True(bApplied);

        var finalValue = await database.StringGetAsync(valueKey);
        Assert.Equal("second", (string?)finalValue);
    }
}
