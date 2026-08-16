using BuildingBlocks;
using Microsoft.Extensions.Logging.Abstractions;
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
            new ThrowingCampaignRepository(),
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

    private sealed class ThrowingCampaignRepository : ICampaignRepository
    {
        public Task<CampaignSnapshot?> FindBestActiveAsync(decimal subtotal, DateTimeOffset now, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("The amount-only path must not resolve a campaign.");
    }

    /// <summary>No campaign is configured - the line-item tests below price against a plain catalog/coupon setup with nothing automatic in play.</summary>
    private sealed class NullCampaignRepository : ICampaignRepository
    {
        public Task<CampaignSnapshot?> FindBestActiveAsync(decimal subtotal, DateTimeOffset now, CancellationToken cancellationToken) =>
            Task.FromResult<CampaignSnapshot?>(null);
    }

    private sealed class FixedCampaignRepository(CampaignSnapshot campaign) : ICampaignRepository
    {
        public Task<CampaignSnapshot?> FindBestActiveAsync(decimal subtotal, DateTimeOffset now, CancellationToken cancellationToken) =>
            Task.FromResult<CampaignSnapshot?>(campaign);
    }

    [Fact]
    public async Task HandleAsyncWithoutIdempotencyKeyCreatesANewOrderOnEveryCall()
    {
        var repository = new FakeOrderRepository();
        var handler = new CreateOrderHandler(repository, BuildPricingService(), TimeProvider.System, NullLogger<CreateOrderHandler>.Instance);
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
        var handler = new CreateOrderHandler(repository, BuildPricingService(), TimeProvider.System, NullLogger<CreateOrderHandler>.Instance);
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
        var handler = new CreateOrderHandler(repository, BuildPricingService(), TimeProvider.System, NullLogger<CreateOrderHandler>.Instance);

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
    public async Task HandleAsyncRejectsAnIdempotencyKeyReusedWithDifferentPayload()
    {
        var repository = new FakeOrderRepository();
        var handler = new CreateOrderHandler(repository, BuildPricingService(), TimeProvider.System, NullLogger<CreateOrderHandler>.Instance);

        var first = await handler.HandleAsync(
            new CreateOrderCommand("customer-1", 10m, "BRL", "correlation-1", "instance-1", "retry-key-1"),
            CancellationToken.None);
        var second = await handler.HandleAsync(
            new CreateOrderCommand("customer-1", 11m, "BRL", "correlation-2", "instance-1", "retry-key-1"),
            CancellationToken.None);

        Assert.False(first.WasReplayed);
        Assert.False(second.WasReplayed);
        Assert.NotNull(second.IdempotencyConflict);
        Assert.Null(second.Order);
        Assert.Equal(1, repository.AddCallCount);
    }

    // ExpectedSubtotal - a line-item checkout this time, since
    // the amount-only path never prices anything to compare against.
    private static OrderPricingService BuildLineItemPricingService(decimal livePrice) =>
        new(new FixedPriceCatalogClient(livePrice),
            new ThrowingCouponRepository(),
            new FixedCustomerRepository(),
            new NullCampaignRepository(),
            new NRulesPricingEngine(Options.Create(new PricingOptions { FlatShippingAmount = 0m })),
            TimeProvider.System);

    private sealed class FixedPriceCatalogClient(decimal price) : ICatalogClient
    {
        public Task<CatalogProductSnapshot?> FindBySkuAsync(string sku, CancellationToken cancellationToken) =>
            Task.FromResult<CatalogProductSnapshot?>(new CatalogProductSnapshot(sku, $"Product {sku}", price, "BRL", sku, "books"));
    }

    private sealed class CountingCatalogClient(decimal price) : ICatalogClient
    {
        public int CallCount { get; private set; }

        public bool ThrowOnCall { get; set; }

        public Task<CatalogProductSnapshot?> FindBySkuAsync(string sku, CancellationToken cancellationToken)
        {
            CallCount++;
            if (ThrowOnCall)
            {
                throw new HttpRequestException("Catalog must not be called for an idempotent replay.");
            }

            return Task.FromResult<CatalogProductSnapshot?>(
                new CatalogProductSnapshot(sku, $"Product {sku}", price, "BRL", sku, "books"));
        }
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
            repository, BuildLineItemPricingService(50m), TimeProvider.System, NullLogger<CreateOrderHandler>.Instance);
        var command = new CreateOrderCommand(
            "customer-1", 0m, null, "correlation-1", "instance-1",
            Items: [new CreateOrderItem("SKU-A", 2)], ExpectedSubtotal: 100m);

        var result = await handler.HandleAsync(command, CancellationToken.None);

        Assert.True(result.IsValid);
        Assert.Null(result.PriceMismatch);
        Assert.NotNull(result.Order);
    }

    /// <summary>
    /// End to end from an active campaign through to the reservation
    /// CreateOrderHandler hands the repository - the same transactional
    /// claim shape as a coupon (see CampaignReservation's own comment),
    /// but built automatically rather than from a shopper-typed code.
    /// </summary>
    [Fact]
    public async Task AnActiveCampaignReservesItsBudgetClaimAlongsideTheOrder()
    {
        var repository = new FakeOrderRepository();
        var now = DateTimeOffset.UtcNow;
        var campaign = new CampaignSnapshot(
            "FLASH20", "R$20 off", 20m, now.AddDays(-1), now.AddDays(30), 0m, ExclusivityGroup: null, BudgetRemaining: 500m);
        var pricing = new OrderPricingService(
            new FixedPriceCatalogClient(50m),
            new ThrowingCouponRepository(),
            new FixedCustomerRepository(),
            new FixedCampaignRepository(campaign),
            new NRulesPricingEngine(Options.Create(new PricingOptions { FlatShippingAmount = 0m })),
            TimeProvider.System);
        var handler = new CreateOrderHandler(repository, pricing, TimeProvider.System, NullLogger<CreateOrderHandler>.Instance);
        var command = new CreateOrderCommand(
            "customer-1", 0m, null, "correlation-1", "instance-1",
            Items: [new CreateOrderItem("SKU-A", 2)]);

        var result = await handler.HandleAsync(command, CancellationToken.None);

        Assert.True(result.IsValid);
        Assert.NotNull(result.Order);
        Assert.Equal("FLASH20", result.Order!.CampaignCode);
        var reservation = Assert.Single(repository.CampaignReservations);
        Assert.Equal("FLASH20", reservation.Code);
        Assert.Equal(20m, reservation.Amount);
        Assert.Equal(result.Order.Id, reservation.OrderId);
    }

    [Fact]
    public async Task AMismatchedExpectedSubtotalIsRejectedWithoutCreatingAnOrder()
    {
        var repository = new FakeOrderRepository();
        // Live catalog price moved to 60.00/unit; the cart last saw 50.00/unit.
        var handler = new CreateOrderHandler(
            repository, BuildLineItemPricingService(60m), TimeProvider.System, NullLogger<CreateOrderHandler>.Instance);
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
            repository, BuildLineItemPricingService(999m), TimeProvider.System, NullLogger<CreateOrderHandler>.Instance);
        var command = new CreateOrderCommand(
            "customer-1", 0m, null, "correlation-1", "instance-1",
            Items: [new CreateOrderItem("SKU-A", 1)], ExpectedSubtotal: null);

        var result = await handler.HandleAsync(command, CancellationToken.None);

        Assert.True(result.IsValid);
        Assert.NotNull(result.Order);
    }

    [Fact]
    public async Task IdempotentReplayIsResolvedBeforeCatalogPricing()
    {
        var repository = new FakeOrderRepository();
        var catalog = new CountingCatalogClient(50m);
        var pricing = new OrderPricingService(
            catalog,
            new ThrowingCouponRepository(),
            new FixedCustomerRepository(),
            new NullCampaignRepository(),
            new NRulesPricingEngine(Options.Create(new PricingOptions { FlatShippingAmount = 0m })),
            TimeProvider.System);
        var handler = new CreateOrderHandler(repository, pricing, TimeProvider.System, NullLogger<CreateOrderHandler>.Instance);
        var command = new CreateOrderCommand(
            "customer-1", 0m, null, "correlation-1", "instance-1", "line-retry-key",
            Items: [new CreateOrderItem("SKU-A", 1)]);

        var first = await handler.HandleAsync(command, CancellationToken.None);
        catalog.ThrowOnCall = true;
        var replay = await handler.HandleAsync(command, CancellationToken.None);

        Assert.NotNull(first.Order);
        Assert.True(replay.WasReplayed);
        Assert.Equal(first.Order.Id, replay.Order!.Id);
        Assert.Equal(1, catalog.CallCount);
    }

    private sealed class FakeOrderRepository : IOrderCreationRepository
    {
        private readonly Dictionary<Guid, Order> _orders = [];
        private readonly Dictionary<(string CustomerId, string Key), OrderIdempotencyEntry> _idempotency = [];

        public int AddCallCount { get; private set; }

        public List<CouponReservation> CouponReservations { get; } = [];

        public List<CampaignReservation> CampaignReservations { get; } = [];

        public Task<OrderWriteResult> AddAsync(
            Order order,
            OutboxMessage outboxMessage,
            CouponReservation? couponReservation,
            OrderIdempotencyClaim? idempotencyClaim,
            CancellationToken cancellationToken,
            CampaignReservation? campaignReservation = null)
        {
            if (idempotencyClaim is not null
                && _idempotency.TryGetValue((idempotencyClaim.CustomerId, idempotencyClaim.IdempotencyKey), out var existing))
            {
                return Task.FromResult(new OrderWriteResult(
                    string.Equals(existing.RequestHash, idempotencyClaim.RequestHash, StringComparison.Ordinal)
                        ? OrderWriteOutcome.Replayed
                        : OrderWriteOutcome.IdempotencyConflict,
                    existing.OrderId,
                    existing.RequestHash));
            }

            AddCallCount++;
            _orders[order.Id] = order;
            if (idempotencyClaim is not null)
            {
                _idempotency[(idempotencyClaim.CustomerId, idempotencyClaim.IdempotencyKey)] =
                    new OrderIdempotencyEntry(order.Id, idempotencyClaim.RequestHash);
            }
            if (couponReservation is not null)
            {
                CouponReservations.Add(couponReservation);
            }

            if (campaignReservation is not null)
            {
                CampaignReservations.Add(campaignReservation);
            }

            return Task.FromResult(new OrderWriteResult(OrderWriteOutcome.Created, order.Id));
        }

        public Task<Order?> FindByIdAsync(Guid id, CancellationToken cancellationToken)
        {
            _orders.TryGetValue(id, out var order);
            return Task.FromResult(order);
        }

        public Task<OrderIdempotencyEntry?> FindIdempotencyAsync(
            string customerId,
            string idempotencyKey,
            CancellationToken cancellationToken)
        {
            _idempotency.TryGetValue((customerId, idempotencyKey), out var entry);
            return Task.FromResult(entry);
        }
    }
}
