using Catalog.Service.Data;
using StackExchange.Redis;
using Testcontainers.Redis;

namespace Catalog.IntegrationTests;

public sealed class BestsellersReaderTests : IAsyncLifetime
{
    private RedisContainer _redis = null!;
    private ConnectionMultiplexer _connectionMultiplexer = null!;
    private BestsellersReader _reader = null!;

    public async Task InitializeAsync()
    {
        _redis = new RedisBuilder("redis:7.4-alpine").Build();
        await _redis.StartAsync();

        _connectionMultiplexer = await ConnectionMultiplexer.ConnectAsync(_redis.GetConnectionString());
        _reader = new BestsellersReader(_connectionMultiplexer);
    }

    public async Task DisposeAsync()
    {
        await _connectionMultiplexer.DisposeAsync();
        await _redis.DisposeAsync();
    }

    [Fact]
    public async Task GetTopAsyncReturnsSkusOrderedByDescendingScore()
    {
        var database = _connectionMultiplexer.GetDatabase();
        await database.SortedSetIncrementAsync("bestsellers:global", "SKU-LOW", 2);
        await database.SortedSetIncrementAsync("bestsellers:global", "SKU-HIGH", 10);
        await database.SortedSetIncrementAsync("bestsellers:global", "SKU-MID", 5);

        var top = await _reader.GetTopAsync(categorySlug: null, limit: 10, CancellationToken.None);

        Assert.Equal(3, top.Count);
        Assert.Equal("SKU-HIGH", top[0].Sku);
        Assert.Equal(10, top[0].UnitsSold);
        Assert.Equal("SKU-MID", top[1].Sku);
        Assert.Equal("SKU-LOW", top[2].Sku);
    }

    [Fact]
    public async Task GetTopAsyncReadsTheCategorySpecificSetWhenACategoryIsGiven()
    {
        var database = _connectionMultiplexer.GetDatabase();
        await database.SortedSetIncrementAsync("bestsellers:global", "SKU-BOOK", 100);
        await database.SortedSetIncrementAsync("bestsellers:category:electronics", "SKU-ELEC", 7);

        var electronicsTop = await _reader.GetTopAsync("electronics", limit: 10, CancellationToken.None);

        Assert.Single(electronicsTop);
        Assert.Equal("SKU-ELEC", electronicsTop[0].Sku);
    }

    [Fact]
    public async Task GetTopAsyncRespectsTheLimit()
    {
        var database = _connectionMultiplexer.GetDatabase();
        for (var i = 0; i < 5; i++)
        {
            await database.SortedSetIncrementAsync("bestsellers:global", $"SKU-{i}", i + 1);
        }

        var top = await _reader.GetTopAsync(categorySlug: null, limit: 2, CancellationToken.None);

        Assert.Equal(2, top.Count);
        Assert.Equal("SKU-4", top[0].Sku);
        Assert.Equal("SKU-3", top[1].Sku);
    }
}
