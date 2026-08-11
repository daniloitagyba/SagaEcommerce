using BuildingBlocks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Orders.Domain;
using Orders.Infrastructure.Data;
using Orders.Worker;
using Polly.Registry;
using Testcontainers.PostgreSql;

namespace Orders.IntegrationTests;

public sealed class OrderStatusStoreTransactionTests : IAsyncLifetime, IDisposable
{
    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder("postgres:17-alpine")
        .WithDatabase("orders_test")
        .WithUsername("test_user")
        .WithPassword("test-password-not-a-secret")
        .Build();

    private OrdersDbContext _dbContext = null!;
    private NpgsqlDataSource _dataSource = null!;
    private OrderStatusStore _store = null!;

    public async Task InitializeAsync()
    {
        await _postgres.StartAsync();
        _dbContext = new OrdersDbContext(
            new DbContextOptionsBuilder<OrdersDbContext>()
                .UseNpgsql(_postgres.GetConnectionString())
                .Options);
        await _dbContext.Database.MigrateAsync();

        _dataSource = NpgsqlDataSource.Create(_postgres.GetConnectionString());
        var pipelineProvider = new ServiceCollection()
            .AddOrdersResilience()
            .BuildServiceProvider()
            .GetRequiredService<ResiliencePipelineProvider<string>>();
        _store = new OrderStatusStore(
            _dataSource,
            new CouponRedemptionStore(),
            new PaymentSettlementRequester(),
            new CustomerTierStore(),
            pipelineProvider);
    }

    public async Task DisposeAsync()
    {
        await _dbContext.DisposeAsync();
        await _dataSource.DisposeAsync();
        await _postgres.DisposeAsync();
    }

    public void Dispose()
    {
        _dbContext?.Dispose();
        _dataSource?.Dispose();
    }

    [Fact]
    public async Task ShippingAndCaptureCommandCommitTogether()
    {
        var orderId = await SeedPickingCardOrderAsync();

        var result = await _store.TryTransitionAsync(
            orderId,
            OrderStatuses.Shipped,
            "transaction-test",
            CancellationToken.None);

        Assert.Equal(StatusTransitionResult.Transitioned, result);
        Assert.Equal(OrderStatuses.Shipped, await CurrentStatusAsync(orderId));

        var command = await _dbContext.OutboxMessages.AsNoTracking().SingleAsync();
        Assert.Equal(nameof(PaymentCaptureRequested), command.EventType);
        Assert.Contains(orderId.ToString(), command.Payload, StringComparison.OrdinalIgnoreCase);
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
