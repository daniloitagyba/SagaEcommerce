using Orders.Worker;
using StackExchange.Redis;
using Testcontainers.Redis;

namespace Orders.IntegrationTests;

public sealed class BestsellersStoreTests : IAsyncLifetime
{
    private readonly RedisContainer _redis = new RedisBuilder("redis:7.4-alpine").Build();
    private ConnectionMultiplexer? _connectionMultiplexer;
    private RedisBestsellersStore _store = null!;

    public async Task InitializeAsync()
    {
        await _redis.StartAsync();
        _connectionMultiplexer = await ConnectionMultiplexer.ConnectAsync(_redis.GetConnectionString());
        _store = new RedisBestsellersStore(_connectionMultiplexer);
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
    public async Task RecordSaleAsyncIncrementsBothTheGlobalAndTheCategorySortedSets()
    {
        await _store.RecordSaleAsync("SKU-1", "electronics", 3, CancellationToken.None);
        await _store.RecordSaleAsync("SKU-2", "electronics", 5, CancellationToken.None);
        await _store.RecordSaleAsync("SKU-1", "electronics", 2, CancellationToken.None);

        var database = _connectionMultiplexer!.GetDatabase();
        var globalTop = await database.SortedSetRangeByScoreWithScoresAsync("bestsellers:global", order: Order.Descending);
        var categoryTop = await database.SortedSetRangeByScoreWithScoresAsync("bestsellers:category:electronics", order: Order.Descending);

        Assert.Equal(2, globalTop.Length);
        Assert.Equal(5.0, (double)globalTop[0].Score);
        Assert.Equal(5.0, (double)globalTop[1].Score);
        Assert.Equal(globalTop.Length, categoryTop.Length);
    }

    [Fact]
    public async Task RecordSaleAsyncSkipsTheCategorySetWhenNoCategoryIsGiven()
    {
        await _store.RecordSaleAsync("SKU-1", categorySlug: null, 1, CancellationToken.None);

        var database = _connectionMultiplexer!.GetDatabase();
        var globalScore = await database.SortedSetScoreAsync("bestsellers:global", "SKU-1");
        var categoryExists = await database.KeyExistsAsync("bestsellers:category:");

        Assert.Equal(1.0, globalScore);
        Assert.False(categoryExists);
    }
}
