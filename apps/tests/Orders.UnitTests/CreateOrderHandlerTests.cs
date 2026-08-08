using BuildingBlocks;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.FeatureManagement;
using Orders.Application.Ports;
using Orders.Application.UseCases.CreateOrder;
using Microsoft.Extensions.Options;
using Orders.Application.Pricing;
using Orders.Domain;

namespace Orders.UnitTests;

public sealed class CreateOrderHandlerTests
{
    // These tests exercise the amount-only path, which never reaches the
    // catalog or the coupon table - collaborators that throw if called are
    // therefore the honest stubs, and would fail loudly if that ever
    // stopped being true.
    private static OrderPricingService BuildPricingService() =>
        new(new ThrowingCatalogClient(),
            new ThrowingCouponRepository(),
            new ThrowingCustomerRepository(),
            new NRulesPricingEngine(Options.Create(new PricingOptions())),
            TimeProvider.System);

    private sealed class ThrowingCatalogClient : ICatalogClient
    {
        public Task<CatalogProductSnapshot?> FindBySkuAsync(string sku, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("The amount-only path must not call the catalog.");
    }

    private sealed class ThrowingCustomerRepository : ICustomerRepository
    {
        public Task<Customer> GetOrCreateAsync(string customerId, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("The amount-only path must not resolve a customer.");
    }

    private sealed class ThrowingCouponRepository : ICouponRepository
    {
        public Task<(CouponSnapshot? Coupon, int CustomerRedemptionCount)> FindAsync(
            string code, string customerId, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("The amount-only path must not resolve a coupon.");
    }

    [Fact]
    public async Task HandleAsyncWithoutIdempotencyKeyCreatesANewOrderOnEveryCall()
    {
        var repository = new FakeOrderRepository();
        var handler = new CreateOrderHandler(repository, new FakeIdempotencyStore(), new FakeFeatureManager(enabled: true), BuildPricingService(), NullLogger<CreateOrderHandler>.Instance);
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
        var handler = new CreateOrderHandler(repository, new FakeIdempotencyStore(), new FakeFeatureManager(enabled: true), BuildPricingService(), NullLogger<CreateOrderHandler>.Instance);
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
        var handler = new CreateOrderHandler(repository, new FakeIdempotencyStore(), new FakeFeatureManager(enabled: true), BuildPricingService(), NullLogger<CreateOrderHandler>.Instance);

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
        var handler = new CreateOrderHandler(repository, new FakeIdempotencyStore(), new FakeFeatureManager(enabled: false), BuildPricingService(), NullLogger<CreateOrderHandler>.Instance);
        var command = new CreateOrderCommand("customer-1", 10m, "BRL", "correlation-1", "instance-1", "retry-key-1");

        var first = await handler.HandleAsync(command, CancellationToken.None);
        var second = await handler.HandleAsync(command, CancellationToken.None);

        Assert.False(first.WasReplayed);
        Assert.False(second.WasReplayed);
        Assert.NotEqual(first.Order!.Id, second.Order!.Id);
        Assert.Equal(2, repository.AddCallCount);
    }

    // ExpectedSubtotal - a line-item checkout this time, since
    // the amount-only path never prices anything to compare against.
    private static OrderPricingService BuildLineItemPricingService(decimal livePrice) =>
        new(new FixedPriceCatalogClient(livePrice),
            new ThrowingCouponRepository(),
            new FixedCustomerRepository(),
            new NRulesPricingEngine(Options.Create(new PricingOptions { FlatShippingAmount = 0m })),
            TimeProvider.System);

    private sealed class FixedPriceCatalogClient(decimal price) : ICatalogClient
    {
        public Task<CatalogProductSnapshot?> FindBySkuAsync(string sku, CancellationToken cancellationToken) =>
            Task.FromResult<CatalogProductSnapshot?>(new CatalogProductSnapshot(sku, $"Product {sku}", price, "BRL", sku, "books"));
    }

    private sealed class FixedCustomerRepository : ICustomerRepository
    {
        public Task<Customer> GetOrCreateAsync(string customerId, CancellationToken cancellationToken) =>
            Task.FromResult(Customer.Create(customerId, DateTimeOffset.UtcNow));
    }

    [Fact]
    public async Task AMatchingExpectedSubtotalCreatesTheOrderNormally()
    {
        var repository = new FakeOrderRepository();
        var handler = new CreateOrderHandler(
            repository, new FakeIdempotencyStore(), new FakeFeatureManager(enabled: false), BuildLineItemPricingService(50m), NullLogger<CreateOrderHandler>.Instance);
        var command = new CreateOrderCommand(
            "customer-1", 0m, null, "correlation-1", "instance-1",
            Items: [new CreateOrderItem("SKU-A", 2)], ExpectedSubtotal: 100m);

        var result = await handler.HandleAsync(command, CancellationToken.None);

        Assert.True(result.IsValid);
        Assert.Null(result.PriceMismatch);
        Assert.NotNull(result.Order);
    }

    [Fact]
    public async Task AMismatchedExpectedSubtotalIsRejectedWithoutCreatingAnOrder()
    {
        var repository = new FakeOrderRepository();
        // Live catalog price moved to 60.00/unit; the cart last saw 50.00/unit.
        var handler = new CreateOrderHandler(
            repository, new FakeIdempotencyStore(), new FakeFeatureManager(enabled: false), BuildLineItemPricingService(60m), NullLogger<CreateOrderHandler>.Instance);
        var command = new CreateOrderCommand(
            "customer-1", 0m, null, "correlation-1", "instance-1",
            Items: [new CreateOrderItem("SKU-A", 2)], ExpectedSubtotal: 100m);

        var result = await handler.HandleAsync(command, CancellationToken.None);

        Assert.False(result.IsValid);
        Assert.NotNull(result.PriceMismatch);
        Assert.Equal(100m, result.PriceMismatch!.ExpectedSubtotal);
        Assert.Equal(120m, result.PriceMismatch.ActualSubtotal);
        Assert.Null(result.Order);
        Assert.Equal(0, repository.AddCallCount);
    }

    [Fact]
    public async Task ANullExpectedSubtotalSkipsTheCheckEntirely()
    {
        var repository = new FakeOrderRepository();
        var handler = new CreateOrderHandler(
            repository, new FakeIdempotencyStore(), new FakeFeatureManager(enabled: false), BuildLineItemPricingService(999m), NullLogger<CreateOrderHandler>.Instance);
        var command = new CreateOrderCommand(
            "customer-1", 0m, null, "correlation-1", "instance-1",
            Items: [new CreateOrderItem("SKU-A", 1)], ExpectedSubtotal: null);

        var result = await handler.HandleAsync(command, CancellationToken.None);

        Assert.True(result.IsValid);
        Assert.NotNull(result.Order);
    }

    private sealed class FakeOrderRepository : IOrderRepository
    {
        private readonly Dictionary<Guid, Order> _orders = [];

        public int AddCallCount { get; private set; }

        public List<CouponReservation> CouponReservations { get; } = [];

        public Task AddAsync(
            Order order,
            OutboxMessage outboxMessage,
            CouponReservation? couponReservation,
            CancellationToken cancellationToken)
        {
            AddCallCount++;
            _orders[order.Id] = order;
            if (couponReservation is not null)
            {
                CouponReservations.Add(couponReservation);
            }

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
