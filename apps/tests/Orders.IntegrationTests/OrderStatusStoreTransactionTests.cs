using BuildingBlocks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Orders.Domain;
using Orders.Infrastructure.Data;
using Orders.Worker;
using Polly.Registry;

namespace Orders.IntegrationTests;

[Collection(PostgresCollectionDefinition.Name)]
public sealed class OrderStatusStoreTransactionTests(PostgresFixture fixture) : IAsyncLifetime, IDisposable
{
    private OrdersDbContext _dbContext = null!;
    private NpgsqlDataSource _dataSource = null!;
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
        var pipelineProvider = new ServiceCollection()
            .AddOrdersResilience()
            .BuildServiceProvider()
            .GetRequiredService<ResiliencePipelineProvider<string>>();
        _store = new OrderStatusStore(
            _dataSource,
            new CouponRedemptionStore(),
            new PromotionCampaignStore(),
            new PaymentSettlementRequester(),
            new CustomerTierStore(),
            pipelineProvider);
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

        // Two rows now, not one: the capture command Shipped-with-Card has
        // always queued, plus OrderStatusChanged - QueueOrderStatusChangedAsync
        // is unconditional (see ApplySideEffectsAsync's own comment), so every
        // legal transition through this store queues it alongside whatever
        // else that target status implies.
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

    /// <summary>
    /// Pins the fix for the loophole CustomerTierStore's own header used to
    /// describe as closed by recording at confirmation alone: confirm,
    /// then cancel, and assert standing is given back rather than kept
    /// permanently. Regression coverage for
    /// docs/architecture/audit-2026-08-15-domain-and-business-rules-review.md
    /// finding 1.
    /// </summary>
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
        // Tier is deliberately not demoted - see Customer.ReverseCompletedOrder's own doc comment.
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
