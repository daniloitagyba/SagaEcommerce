using BuildingBlocks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Orders.Application.Ports;
using Orders.Domain;
using Orders.Infrastructure.Data;
using Orders.Infrastructure.Persistence;
using Orders.Worker;
using Polly.Registry;

namespace Orders.IntegrationTests;

[Collection(PostgresCollectionDefinition.Name)]
public sealed class OrderStatusStoreTransactionTests(PostgresFixture fixture) : IAsyncLifetime, IDisposable
{
    private OrdersDbContext _dbContext = null!;
    private NpgsqlDataSource _dataSource = null!;
    private ResiliencePipelineProvider<string> _pipelineProvider = null!;
    private OrderStatusStore _store = null!;

    public async Task InitializeAsync()
    {
        var connectionString = await fixture.CreateSchemaAsync(nameof(OrderStatusStoreTransactionTests));
        _dbContext = new OrdersDbContext(
            new DbContextOptionsBuilder<OrdersDbContext>()
                .UseNpgsql(connectionString)
                .Options);
        await _dbContext.Database.MigrateAsync();

        _dataSource = NpgsqlDataSource.Create(connectionString);
        _pipelineProvider = new ServiceCollection()
            .AddOrdersResilience()
            .BuildServiceProvider()
            .GetRequiredService<ResiliencePipelineProvider<string>>();
        _store = new OrderStatusStore(
            _dataSource,
            new CouponRedemptionStore(),
            new PromotionCampaignStore(),
            new PaymentSettlementRequester(),
            new CustomerTierStore(),
            _pipelineProvider);
    }

    public async Task DisposeAsync()
    {
        await _dbContext.DisposeAsync();
        await _dataSource.DisposeAsync();
    }

    public void Dispose()
    {
        _dbContext?.Dispose();
        _dataSource?.Dispose();
    }

    [Fact]
    public async Task ShippingAndCaptureCommandAndStatusChangedCommitTogether()
    {
        var orderId = await SeedPickingCardOrderAsync();

        var result = await _store.TryTransitionAsync(
            orderId,
            OrderStatuses.Shipped,
            "transaction-test",
            CancellationToken.None);

        Assert.Equal(StatusTransitionResult.Transitioned, result);
        Assert.Equal(OrderStatuses.Shipped, await CurrentStatusAsync(orderId));

        var messages = await _dbContext.OutboxMessages.AsNoTracking().ToListAsync();
        Assert.Equal(2, messages.Count);

        var command = Assert.Single(messages, message => message.EventType == nameof(PaymentCaptureRequested));
        Assert.Contains(orderId.ToString(), command.Payload, StringComparison.OrdinalIgnoreCase);

        var statusChanged = Assert.Single(messages, message => message.EventType == nameof(OrderStatusChanged));
        Assert.Contains(orderId.ToString(), statusChanged.Payload, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(OrderStatuses.Shipped, statusChanged.Payload, StringComparison.Ordinal);
    }

    [Fact]
    public async Task OutboxFailureRollsBackStatusTransition()
    {
        var orderId = await SeedPickingCardOrderAsync();
        var correlationIdBeyondTheOutboxLimit = new string('x', 129);

        await Assert.ThrowsAsync<PostgresException>(() => _store.TryTransitionAsync(
            orderId,
            OrderStatuses.Shipped,
            correlationIdBeyondTheOutboxLimit,
            CancellationToken.None));

        Assert.Equal(OrderStatuses.Picking, await CurrentStatusAsync(orderId));
        Assert.Empty(await _dbContext.OutboxMessages.AsNoTracking().ToListAsync());
    }

    [Fact]
    public async Task ConfirmationAndCustomerStandingCommitTogether()
    {
        const string customerId = "standing-customer";
        _dbContext.Customers.Add(Customer.Create(customerId, DateTimeOffset.UtcNow.AddDays(-30)));
        var order = Order.Create(customerId, 1_250m, "BRL", DateTimeOffset.UtcNow);
        _dbContext.Orders.Add(order);
        await _dbContext.SaveChangesAsync();

        var result = await _store.TryConfirmAsync(order.Id, "standing-test", CancellationToken.None);

        _dbContext.ChangeTracker.Clear();
        var customer = await _dbContext.Customers.AsNoTracking().SingleAsync(item => item.Id == customerId);
        Assert.True(result);
        Assert.Equal(OrderStatuses.Confirmed, await CurrentStatusAsync(order.Id));
        Assert.Equal(1_250m, customer.LifetimeSpend);
        Assert.Equal(1, customer.CompletedOrderCount);
        Assert.Equal(CustomerTiers.Silver, customer.Tier);
    }

    /// <summary>Pins the fix for the loophole where standing recorded at confirmation was never reversed on cancellation. Regression coverage for docs/architecture/audit-2026-08-15-domain-and-business-rules-review.md finding 1.</summary>
    [Fact]
    public async Task ConfirmThenCancelReversesCustomerStanding()
    {
        const string customerId = "confirm-then-cancel-customer";
        _dbContext.Customers.Add(Customer.Create(customerId, DateTimeOffset.UtcNow.AddDays(-30)));
        var order = Order.Create(customerId, 1_250m, "BRL", DateTimeOffset.UtcNow);
        _dbContext.Orders.Add(order);
        await _dbContext.SaveChangesAsync();

        Assert.True(await _store.TryConfirmAsync(order.Id, "standing-test", CancellationToken.None));

        _dbContext.ChangeTracker.Clear();
        var confirmedCustomer = await _dbContext.Customers.AsNoTracking().SingleAsync(item => item.Id == customerId);
        Assert.Equal(1_250m, confirmedCustomer.LifetimeSpend);
        Assert.Equal(1, confirmedCustomer.CompletedOrderCount);
        Assert.Equal(CustomerTiers.Silver, confirmedCustomer.Tier);

        Assert.True(await _store.TryCancelAsync(order.Id, "standing-test", CancellationToken.None));

        _dbContext.ChangeTracker.Clear();
        var cancelledCustomer = await _dbContext.Customers.AsNoTracking().SingleAsync(item => item.Id == customerId);
        Assert.Equal(OrderStatuses.Cancelled, await CurrentStatusAsync(order.Id));
        Assert.Equal(0m, cancelledCustomer.LifetimeSpend);
        Assert.Equal(0, cancelledCustomer.CompletedOrderCount);
        Assert.Equal(CustomerTiers.Silver, cancelledCustomer.Tier);
    }

    /// <summary>A cancellation from Created never recorded any standing in the first place, so reversing it must be a no-op, not a negative balance.</summary>
    [Fact]
    public async Task CancellingAnUnconfirmedOrderDoesNotTouchCustomerStanding()
    {
        const string customerId = "never-confirmed-customer";
        _dbContext.Customers.Add(Customer.Create(customerId, DateTimeOffset.UtcNow.AddDays(-30)));
        var order = Order.Create(customerId, 500m, "BRL", DateTimeOffset.UtcNow);
        _dbContext.Orders.Add(order);
        await _dbContext.SaveChangesAsync();

        Assert.True(await _store.TryCancelAsync(order.Id, "standing-test", CancellationToken.None));

        _dbContext.ChangeTracker.Clear();
        var customer = await _dbContext.Customers.AsNoTracking().SingleAsync(item => item.Id == customerId);
        Assert.Equal(OrderStatuses.Cancelled, await CurrentStatusAsync(order.Id));
        Assert.Equal(0m, customer.LifetimeSpend);
        Assert.Equal(0, customer.CompletedOrderCount);
    }

    [Fact]
    public async Task ApiAndSagaConfirmationHaveEquivalentStatusAndLoyaltyEffects()
    {
        const decimal amount = 1_250m;
        var apiOrder = Order.Create("api-transition-customer", amount, "BRL", DateTimeOffset.UtcNow);
        var sagaOrder = Order.Create("saga-transition-customer", amount, "BRL", DateTimeOffset.UtcNow);
        await _dbContext.Customers.AddRangeAsync(
            Customer.Create(apiOrder.CustomerId, DateTimeOffset.UtcNow.AddDays(-30)),
            Customer.Create(sagaOrder.CustomerId, DateTimeOffset.UtcNow.AddDays(-30)));
        await _dbContext.Orders.AddRangeAsync(apiOrder, sagaOrder);
        await _dbContext.SaveChangesAsync();

        var apiRepository = new EfOrderStatusRepository(new OrderTransitionExecutor(_dataSource, _pipelineProvider));
        var apiResult = await apiRepository.TryTransitionAsync(
            apiOrder.Id,
            OrderStatuses.Confirmed,
            OrderStatuses.PredecessorsOf(OrderStatuses.Confirmed),
            OrderStatuses.SettlementActionFor(OrderStatuses.Confirmed),
            "api-transition",
            CancellationToken.None);
        var sagaResult = await _store.TryTransitionAsync(
            sagaOrder.Id,
            OrderStatuses.Confirmed,
            "saga-transition",
            CancellationToken.None);

        Assert.Equal(OrderTransitionOutcome.Advanced, apiResult.Outcome);
        Assert.Equal(StatusTransitionResult.Transitioned, sagaResult);
        Assert.Equal(OrderStatuses.Confirmed, await CurrentStatusAsync(apiOrder.Id));
        Assert.Equal(OrderStatuses.Confirmed, await CurrentStatusAsync(sagaOrder.Id));

        _dbContext.ChangeTracker.Clear();
        var customers = await _dbContext.Customers.AsNoTracking()
            .Where(customer => customer.Id == apiOrder.CustomerId || customer.Id == sagaOrder.CustomerId)
            .OrderBy(customer => customer.Id)
            .ToListAsync();
        Assert.Equal(2, customers.Count);
        Assert.All(customers, customer =>
        {
            Assert.Equal(amount, customer.LifetimeSpend);
            Assert.Equal(1, customer.CompletedOrderCount);
            Assert.Equal(CustomerTiers.Silver, customer.Tier);
        });

        var events = await _dbContext.OutboxMessages.AsNoTracking()
            .Where(message => message.EventType == nameof(OrderStatusChanged))
            .ToListAsync();
        Assert.Equal(2, events.Count);
        Assert.Contains(events, message => message.Payload.Contains(apiOrder.Id.ToString(), StringComparison.OrdinalIgnoreCase));
        Assert.Contains(events, message => message.Payload.Contains(sagaOrder.Id.ToString(), StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task ApiAndSagaShippingHaveEquivalentStatusAndCaptureEffects()
    {
        var apiOrder = await SeedPickingCardOrderAsync();
        var sagaOrder = await SeedPickingCardOrderAsync();
        var apiRepository = new EfOrderStatusRepository(new OrderTransitionExecutor(_dataSource, _pipelineProvider));

        var apiResult = await apiRepository.TryTransitionAsync(
            apiOrder,
            OrderStatuses.Shipped,
            OrderStatuses.PredecessorsOf(OrderStatuses.Shipped),
            OrderStatuses.SettlementActionFor(OrderStatuses.Shipped),
            "api-shipping",
            CancellationToken.None);
        var sagaResult = await _store.TryTransitionAsync(
            sagaOrder,
            OrderStatuses.Shipped,
            "saga-shipping",
            CancellationToken.None);

        Assert.Equal(OrderTransitionOutcome.Advanced, apiResult.Outcome);
        Assert.Equal(StatusTransitionResult.Transitioned, sagaResult);
        Assert.Equal(OrderStatuses.Shipped, await CurrentStatusAsync(apiOrder));
        Assert.Equal(OrderStatuses.Shipped, await CurrentStatusAsync(sagaOrder));

        _dbContext.ChangeTracker.Clear();
        var messages = await _dbContext.OutboxMessages.AsNoTracking().ToListAsync();
        Assert.Equal(4, messages.Count);
        Assert.Equal(2, messages.Count(message => message.EventType == nameof(OrderStatusChanged)));
        Assert.Equal(2, messages.Count(message => message.EventType == nameof(PaymentCaptureRequested)));
        Assert.Contains(messages, message => message.Payload.Contains(apiOrder.ToString(), StringComparison.OrdinalIgnoreCase));
        Assert.Contains(messages, message => message.Payload.Contains(sagaOrder.ToString(), StringComparison.OrdinalIgnoreCase));
    }

    private async Task<Guid> SeedPickingCardOrderAsync()
    {
        var order = Order.Create("transaction-customer", 149.90m, "BRL", DateTimeOffset.UtcNow);
        _dbContext.Orders.Add(order);
        await _dbContext.SaveChangesAsync();

        await using var command = _dataSource.CreateCommand(
            "UPDATE orders SET status = 'Picking', payment_method = 'Card' WHERE id = @id");
        command.Parameters.AddWithValue("id", order.Id);
        await command.ExecuteNonQueryAsync();
        return order.Id;
    }

    private async Task<string> CurrentStatusAsync(Guid orderId)
    {
        await using var command = _dataSource.CreateCommand("SELECT status FROM orders WHERE id = @id");
        command.Parameters.AddWithValue("id", orderId);
        return (string)(await command.ExecuteScalarAsync())!;
    }
}
