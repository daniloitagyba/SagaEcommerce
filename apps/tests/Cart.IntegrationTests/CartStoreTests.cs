using BuildingBlocks;
using Cart.Service.Data;
using Cart.Service.Domain;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Polly.Registry;
using StackExchange.Redis;
using Testcontainers.Redis;

namespace Cart.IntegrationTests;

public sealed class CartStoreTests : IAsyncLifetime
{
    private readonly RedisContainer _redis = new RedisBuilder("redis:7.4-alpine").Build();
    private readonly ResiliencePipelineProvider<string> _pipelineProvider = new ServiceCollection()
        .AddOrdersResilience()
        .AddCartRedisResilience()
        .BuildServiceProvider()
        .GetRequiredService<ResiliencePipelineProvider<string>>();
    private ConnectionMultiplexer? _connectionMultiplexer;
    private CartStore _store = null!;

    public async Task InitializeAsync()
    {
        await _redis.StartAsync();
        _connectionMultiplexer = await ConnectionMultiplexer.ConnectAsync(_redis.GetConnectionString());
        _store = new CartStore(_connectionMultiplexer, _pipelineProvider, Options.Create(new CartOptions { TimeToLiveSeconds = 60 }));
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
    public async Task UpsertThenGetReturnsTheSameItemAndSetsATtl()
    {
        var cartId = Guid.NewGuid().ToString("N");
        var item = new CartLineItem("SKU-1", 2, 49.90m, "BRL", "Widget", DateTimeOffset.UtcNow);

        await _store.UpsertItemAsync(cartId, item, CancellationToken.None);
        var items = await _store.GetAsync(cartId, CancellationToken.None);
        var ttl = await _store.GetTimeToLiveAsync(cartId, CancellationToken.None);

        Assert.Single(items);
        Assert.Equal(item, items[0]);
        Assert.NotNull(ttl);
        Assert.True(ttl!.Value.TotalSeconds is > 0 and <= 60);
    }

    [Fact]
    public async Task UpsertWithTheSameSkuOverwritesQuantityRatherThanDuplicating()
    {
        var cartId = Guid.NewGuid().ToString("N");
        var addedAt = DateTimeOffset.UtcNow;
        var item = new CartLineItem("SKU-1", 1, 10m, "BRL", "Widget", addedAt);

        await _store.UpsertItemAsync(cartId, item, CancellationToken.None);
        await _store.UpsertItemAsync(cartId, item.WithQuantity(5), CancellationToken.None);
        var items = await _store.GetAsync(cartId, CancellationToken.None);

        Assert.Single(items);
        Assert.Equal(5, items[0].Quantity);
    }

    [Fact]
    public async Task RemoveItemAsyncDeletesOnlyTheGivenSkuAndClearAsyncDeletesTheWholeCart()
    {
        var cartId = Guid.NewGuid().ToString("N");
        await _store.UpsertItemAsync(cartId, new CartLineItem("SKU-1", 1, 10m, "BRL", "A", DateTimeOffset.UtcNow), CancellationToken.None);
        await _store.UpsertItemAsync(cartId, new CartLineItem("SKU-2", 1, 20m, "BRL", "B", DateTimeOffset.UtcNow), CancellationToken.None);

        var removed = await _store.RemoveItemAsync(cartId, "SKU-1", CancellationToken.None);
        var afterRemove = await _store.GetAsync(cartId, CancellationToken.None);

        Assert.True(removed);
        Assert.Single(afterRemove);
        Assert.Equal("SKU-2", afterRemove[0].Sku);

        var cleared = await _store.ClearAsync(cartId, CancellationToken.None);
        var afterClear = await _store.GetAsync(cartId, CancellationToken.None);

        Assert.True(cleared);
        Assert.Empty(afterClear);
    }

    [Fact]
    public async Task GetAsyncOnAnUnknownCartReturnsAnEmptyListRatherThanThrowing()
    {
        var items = await _store.GetAsync(Guid.NewGuid().ToString("N"), CancellationToken.None);

        Assert.Empty(items);
    }

    [Fact]
    public async Task ConcurrentUpsertsAtomicallyPreserveEveryItemAndVersionIncrement()
    {
        var ownerId = Guid.NewGuid().ToString("N");
        var now = DateTimeOffset.UtcNow;

        await Task.WhenAll(Enumerable.Range(1, 25).Select(index =>
            _store.UpsertItemAsync(
                ownerId,
                new CartLineItem($"SKU-{index:00}", 1, index, "BRL", $"Item {index}", now),
                CancellationToken.None)));

        var snapshot = await _store.GetSnapshotAsync(ownerId, CancellationToken.None);
        Assert.Equal(25, snapshot.Items.Count);
        Assert.Equal(25, snapshot.Version);
        Assert.False(string.IsNullOrWhiteSpace(snapshot.CartId));
        Assert.NotNull(snapshot.TimeToLive);
    }

    [Fact]
    public async Task ConditionalClearPreservesACartThatChangedAfterCheckoutRead()
    {
        var ownerId = Guid.NewGuid().ToString("N");
        await _store.UpsertItemAsync(
            ownerId,
            new CartLineItem("SKU-1", 1, 10m, "BRL", "A", DateTimeOffset.UtcNow),
            CancellationToken.None);
        var checkoutSnapshot = await _store.GetSnapshotAsync(ownerId, CancellationToken.None);

        await _store.UpsertItemAsync(
            ownerId,
            new CartLineItem("SKU-2", 1, 20m, "BRL", "B", DateTimeOffset.UtcNow),
            CancellationToken.None);
        var staleClear = await _store.ClearIfVersionAsync(
            ownerId, checkoutSnapshot.CartId, checkoutSnapshot.Version, CancellationToken.None);

        Assert.False(staleClear);
        var current = await _store.GetSnapshotAsync(ownerId, CancellationToken.None);
        Assert.Equal(2, current.Items.Count);

        var currentClear = await _store.ClearIfVersionAsync(
            ownerId, current.CartId, current.Version, CancellationToken.None);
        Assert.True(currentClear);
        Assert.Empty((await _store.GetSnapshotAsync(ownerId, CancellationToken.None)).Items);
    }

    [Fact]
    public async Task ReplayingTheSameOfflineMergeIsIdempotentWithPersistedCausalState()
    {
        var ownerId = Guid.NewGuid().ToString("N");
        var now = DateTimeOffset.UtcNow;
        await _store.UpsertItemAsync(
            ownerId,
            new CartLineItem("SKU-ONLINE", 1, 10m, "BRL", "Online", now),
            CancellationToken.None);
        var offlineState = CartCrdtState.Empty.Increase(
            "SKU-OFFLINE",
            "offline-device",
            2,
            dotCounter: 1,
            new CartItemMetadata("Offline", 20m, "BRL", now));

        await _store.MergeAsync(ownerId, offlineState, CancellationToken.None);
        await _store.MergeAsync(ownerId, offlineState, CancellationToken.None);

        var snapshot = await _store.GetSnapshotAsync(ownerId, CancellationToken.None);
        Assert.Equal(2, snapshot.Items.Count);
        Assert.Equal(2, snapshot.Items.Single(item => item.Sku == "SKU-OFFLINE").Quantity);
    }

    [Fact]
    public async Task ReAddingARemovedSkuUsesTheNewRequestedQuantity()
    {
        var ownerId = Guid.NewGuid().ToString("N");
        var original = new CartLineItem("SKU-1", 5, 10m, "BRL", "Original", DateTimeOffset.UtcNow);
        await _store.UpsertItemAsync(ownerId, original, CancellationToken.None);
        Assert.True(await _store.RemoveItemAsync(ownerId, original.Sku, CancellationToken.None));

        await _store.UpsertItemAsync(
            ownerId,
            new CartLineItem("SKU-1", 1, 12m, "BRL", "Re-added", DateTimeOffset.UtcNow),
            CancellationToken.None);

        var item = Assert.Single((await _store.GetSnapshotAsync(ownerId, CancellationToken.None)).Items);
        Assert.Equal(1, item.Quantity);
        Assert.Equal(12m, item.UnitPrice);
    }
}
