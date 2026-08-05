using BuildingBlocks;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.FeatureManagement;
using Orders.Application.Ports;
using Orders.Application.UseCases.CreateOrder;
using Orders.Domain;

namespace Orders.UnitTests;

public sealed class CreateOrderHandlerTests
{
    [Fact]
    public async Task HandleAsyncWithoutIdempotencyKeyCreatesANewOrderOnEveryCall()
    {
        var repository = new FakeOrderRepository();
        var handler = new CreateOrderHandler(repository, new FakeIdempotencyStore(), new FakeFeatureManager(enabled: true), NullLogger<CreateOrderHandler>.Instance);
        var command = new CreateOrderCommand("customer-1", 10m, "BRL", "correlation-1", "instance-1");

        var first = await handler.HandleAsync(command, CancellationToken.None);
        var second = await handler.HandleAsync(command, CancellationToken.None);

        Assert.False(first.WasReplayed);
        Assert.False(second.WasReplayed);
        Assert.NotEqual(first.Order!.Id, second.Order!.Id);
        Assert.Equal(2, repository.AddCallCount);
    }

    [Fact]
    public async Task HandleAsyncWithSameIdempotencyKeyReplaysTheFirstResultInsteadOfCreatingAgain()
    {
        var repository = new FakeOrderRepository();
        var handler = new CreateOrderHandler(repository, new FakeIdempotencyStore(), new FakeFeatureManager(enabled: true), NullLogger<CreateOrderHandler>.Instance);
        var command = new CreateOrderCommand("customer-1", 10m, "BRL", "correlation-1", "instance-1", "retry-key-1");

        var first = await handler.HandleAsync(command, CancellationToken.None);
        var second = await handler.HandleAsync(command, CancellationToken.None);

        Assert.False(first.WasReplayed);
        Assert.True(second.WasReplayed);
        Assert.Equal(first.Order!.Id, second.Order!.Id);
        Assert.Equal(1, repository.AddCallCount);
    }

    [Fact]
    public async Task HandleAsyncWithDifferentIdempotencyKeysCreatesIndependentOrders()
    {
        var repository = new FakeOrderRepository();
        var handler = new CreateOrderHandler(repository, new FakeIdempotencyStore(), new FakeFeatureManager(enabled: true), NullLogger<CreateOrderHandler>.Instance);

        var first = await handler.HandleAsync(
            new CreateOrderCommand("customer-1", 10m, "BRL", "correlation-1", "instance-1", "key-a"),
            CancellationToken.None);
        var second = await handler.HandleAsync(
            new CreateOrderCommand("customer-1", 10m, "BRL", "correlation-2", "instance-1", "key-b"),
            CancellationToken.None);

        Assert.NotEqual(first.Order!.Id, second.Order!.Id);
        Assert.Equal(2, repository.AddCallCount);
    }

    [Fact]
    public async Task HandleAsyncIgnoresTheIdempotencyKeyWhenTheFeatureFlagIsDisabled()
    {
        var repository = new FakeOrderRepository();
        var handler = new CreateOrderHandler(repository, new FakeIdempotencyStore(), new FakeFeatureManager(enabled: false), NullLogger<CreateOrderHandler>.Instance);
        var command = new CreateOrderCommand("customer-1", 10m, "BRL", "correlation-1", "instance-1", "retry-key-1");

        var first = await handler.HandleAsync(command, CancellationToken.None);
        var second = await handler.HandleAsync(command, CancellationToken.None);

        Assert.False(first.WasReplayed);
        Assert.False(second.WasReplayed);
        Assert.NotEqual(first.Order!.Id, second.Order!.Id);
        Assert.Equal(2, repository.AddCallCount);
    }

    private sealed class FakeOrderRepository : IOrderRepository
    {
        private readonly Dictionary<Guid, Order> _orders = [];

        public int AddCallCount { get; private set; }

        public Task AddAsync(Order order, OutboxMessage outboxMessage, CancellationToken cancellationToken)
        {
            AddCallCount++;
            _orders[order.Id] = order;
            return Task.CompletedTask;
        }

        public Task<Order?> FindByIdAsync(Guid id, CancellationToken cancellationToken)
        {
            _orders.TryGetValue(id, out var order);
            return Task.FromResult(order);
        }
    }

    private sealed class FakeIdempotencyStore : IIdempotencyStore
    {
        private readonly Dictionary<string, CachedOrder> _entries = [];

        public async Task<IdempotencyLookup> GetOrCreateAsync(
            string idempotencyKey,
            Func<CancellationToken, Task<CachedOrder>> factory,
            CancellationToken cancellationToken)
        {
            if (_entries.TryGetValue(idempotencyKey, out var existing))
            {
                return new IdempotencyLookup(existing, WasReplayed: true);
            }

            var created = await factory(cancellationToken);
            _entries[idempotencyKey] = created;
            return new IdempotencyLookup(created, WasReplayed: false);
        }
    }

    private sealed class FakeFeatureManager(bool enabled) : IFeatureManager
    {
        public IAsyncEnumerable<string> GetFeatureNamesAsync() => AsyncEnumerable.Empty<string>();

        public Task<bool> IsEnabledAsync(string feature) => Task.FromResult(enabled);

        public Task<bool> IsEnabledAsync<TContext>(string feature, TContext context) => Task.FromResult(enabled);
    }
}
