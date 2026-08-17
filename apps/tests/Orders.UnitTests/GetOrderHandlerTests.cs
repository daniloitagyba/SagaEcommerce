using BuildingBlocks;
using Orders.Application.Ports;
using Orders.Application.UseCases.GetOrder;
using Orders.Domain;

namespace Orders.UnitTests;

/// <summary>The read path the UI uses most; hand-rolled fakes for IOrderCache/IOrderRepository since the handler's own logic has no database dependency of its own.</summary>
public sealed class GetOrderHandlerTests
{
    [Fact]
    public async Task AnOrderFoundInTheRepositoryIsMappedIntoACachedOrder()
    {
        var order = Order.Create("customer-1", 99.90m, "BRL", DateTimeOffset.UtcNow);
        var repository = new FakeOrderRepository { OrderToReturn = order };
        var handler = new GetOrderHandler(new PassThroughCache(), repository);

        var result = await handler.HandleAsync(order.Id, CancellationToken.None);

        Assert.NotNull(result.Order);
        Assert.Equal(order.Id, result.Order!.Id);
        Assert.Equal(order.CustomerId, result.Order.CustomerId);
        Assert.Equal(order.Amount, result.Order.Amount);
        Assert.Equal(order.Currency, result.Order.Currency);
        Assert.Equal(order.Status, result.Order.Status);
        Assert.Equal(CacheLookupResult.Miss, result.LookupResult);
    }

    [Fact]
    public async Task AnUnknownOrderIdReturnsNullWithoutCallingTheRepositoryTwice()
    {
        var repository = new FakeOrderRepository { OrderToReturn = null };
        var handler = new GetOrderHandler(new PassThroughCache(), repository);

        var result = await handler.HandleAsync(Guid.NewGuid(), CancellationToken.None);

        Assert.Null(result.Order);
        Assert.Equal(1, repository.FindByIdCallCount);
    }

    [Fact]
    public async Task ACacheHitNeverTouchesTheRepositoryAtAll()
    {
        var cachedOrder = new CachedOrder(Guid.NewGuid(), "customer-2", 10m, "BRL", "Confirmed", DateTimeOffset.UtcNow);
        var repository = new FakeOrderRepository { OrderToReturn = Order.Create("should-not-be-read", 1m, "BRL", DateTimeOffset.UtcNow) };
        var handler = new GetOrderHandler(new FixedResultCache(new CacheLookup(cachedOrder, CacheLookupResult.Hit)), repository);

        var result = await handler.HandleAsync(cachedOrder.Id, CancellationToken.None);

        Assert.Same(cachedOrder, result.Order);
        Assert.Equal(CacheLookupResult.Hit, result.LookupResult);
        Assert.Equal(0, repository.FindByIdCallCount);
    }

    /// <summary>Runs the real factory the handler passes in, the same way RedisOrderCache would - Miss/Hit/Bypassed all come from the factory actually being invoked or not.</summary>
    private sealed class PassThroughCache : IOrderCache
    {
        public async Task<CacheLookup> GetOrCreateAsync(Guid id, Func<CancellationToken, Task<CachedOrder?>> factory, CancellationToken cancellationToken)
        {
            var order = await factory(cancellationToken);
            return new CacheLookup(order, CacheLookupResult.Miss);
        }

        public Task InvalidateAsync(Guid id, CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private sealed class FixedResultCache(CacheLookup result) : IOrderCache
    {
        public Task<CacheLookup> GetOrCreateAsync(Guid id, Func<CancellationToken, Task<CachedOrder?>> factory, CancellationToken cancellationToken) =>
            Task.FromResult(result);

        public Task InvalidateAsync(Guid id, CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private sealed class FakeOrderRepository : IOrderRepository
    {
        public Order? OrderToReturn { get; init; }

        public int FindByIdCallCount { get; private set; }

        public Task<Order?> FindByIdAsync(Guid id, CancellationToken cancellationToken)
        {
            FindByIdCallCount++;
            return Task.FromResult(OrderToReturn);
        }
    }
}
