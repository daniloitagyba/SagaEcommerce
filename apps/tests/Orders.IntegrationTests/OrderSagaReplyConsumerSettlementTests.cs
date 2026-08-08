using BuildingBlocks;
using Confluent.Kafka;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Npgsql;
using Orders.Domain;
using Orders.Infrastructure.Data;
using Orders.Worker;
using Polly.Registry;
using Testcontainers.PostgreSql;
using Testcontainers.Redpanda;

namespace Orders.IntegrationTests;

/// <summary>
/// Milestone 76: reproduces the exact race the milestone exists to close -
/// an authorization expires (the sweeper, or a capture command that arrives
/// too late) after the order has already shipped. Before this milestone,
/// nothing consumed payments.settlement-replied.v1 at all: the order would
/// sit in Shipped forever, eventually Delivered, having never been charged,
/// with no record anywhere that it went wrong.
/// </summary>
public sealed class OrderSagaReplyConsumerSettlementTests : IAsyncLifetime, IDisposable
{
    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder("postgres:17-alpine")
        .WithDatabase("orders_test")
        .WithUsername("test_user")
        .WithPassword("test-password-not-a-secret")
        .Build();

    private readonly RedpandaContainer _redpanda =
        new RedpandaBuilder("docker.redpanda.com/redpandadata/redpanda:v26.2.1").Build();

    private NpgsqlDataSource _dataSource = null!;
    private OrdersDbContext _dbContext = null!;
    private OrderStatusStore _orderStatusStore = null!;
    private IProducer<string, string> _producer = null!;

    public async Task InitializeAsync()
    {
        await Task.WhenAll(_postgres.StartAsync(), _redpanda.StartAsync());

        var dbOptions = new DbContextOptionsBuilder<OrdersDbContext>().UseNpgsql(_postgres.GetConnectionString()).Options;
        _dbContext = new OrdersDbContext(dbOptions);
        await _dbContext.Database.MigrateAsync();

        var pipelineProvider = new ServiceCollection()
            .AddOrdersResilience()
            .BuildServiceProvider()
            .GetRequiredService<ResiliencePipelineProvider<string>>();

        _dataSource = NpgsqlDataSource.Create(_postgres.GetConnectionString());
        _producer = new ProducerBuilder<string, string>(new ProducerConfig { BootstrapServers = _redpanda.GetBootstrapAddress() }).Build();

        var couponStore = new CouponRedemptionStore(_dataSource, pipelineProvider, NullLogger<CouponRedemptionStore>.Instance);
        var settlementRequester = new PaymentSettlementRequester(_producer, Options.Create(new PaymentSettlementRequestOptions()), NullLogger<PaymentSettlementRequester>.Instance);
        var customerTierStore = new CustomerTierStore(_dataSource, pipelineProvider, NullLogger<CustomerTierStore>.Instance);
        _orderStatusStore = new OrderStatusStore(_dataSource, couponStore, settlementRequester, customerTierStore, pipelineProvider);
    }

    public async Task DisposeAsync()
    {
        await _dbContext.DisposeAsync();
        await _dataSource.DisposeAsync();
        await Task.WhenAll(_postgres.DisposeAsync().AsTask(), _redpanda.DisposeAsync().AsTask());
    }

    public void Dispose() => _producer.Dispose();

    [Fact]
    public async Task AShippedOrderMovesToFulfillmentHoldWhenSettlementComesBackExpiredInsteadOfCaptured()
    {
        var orderId = await SeedShippedOrderAsync();
        var consumer = CreateConsumer();

        await consumer.DispatchAsync(SettlementReply(orderId, PaymentStates.Expired), CancellationToken.None);

        var status = await CurrentStatusAsync(orderId);
        Assert.Equal(OrderStatuses.FulfillmentHold, status);
    }

    [Fact]
    public async Task AShippedOrderIsUntouchedWhenSettlementComesBackCaptured()
    {
        var orderId = await SeedShippedOrderAsync();
        var consumer = CreateConsumer();

        await consumer.DispatchAsync(SettlementReply(orderId, PaymentStates.Captured), CancellationToken.None);

        var status = await CurrentStatusAsync(orderId);
        Assert.Equal(OrderStatuses.Shipped, status);
    }

    private async Task<Guid> SeedShippedOrderAsync()
    {
        var order = Order.Create("settlement-test-customer", 149.90m, "BRL", DateTimeOffset.UtcNow);
        _dbContext.Orders.Add(order);
        await _dbContext.SaveChangesAsync();

        // Test fixture only: jumps the row straight to Shipped rather than
        // walking every legal transition, since this test is about the
        // settlement-reply reaction, not the lifecycle itself (that's
        // OrderStatusTransitionTests).
        await using var command = _dataSource.CreateCommand("UPDATE orders SET status = 'Shipped' WHERE id = @id");
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

    private OrderSagaReplyConsumer CreateConsumer() =>
        new(
            Options.Create(new SagaOrchestrationOptions()),
            _producer,
            new SagaOrchestrationStore(_dataSource, new ServiceCollection().AddOrdersResilience().BuildServiceProvider().GetRequiredService<ResiliencePipelineProvider<string>>()),
            _orderStatusStore,
            new NoOpCacheInvalidator(),
            new NoOpBestsellersStore(),
            new NoOpCatalogClient(),
            NullLogger<OrderSagaReplyConsumer>.Instance);

    private static ConsumeResult<string, string> SettlementReply(Guid orderId, string state)
    {
        var reply = new PaymentSettlementReplied(orderId, Guid.NewGuid(), state, 149.90m, "BRL", "settlement-test-correlation", DateTimeOffset.UtcNow);
        return new ConsumeResult<string, string>
        {
            Topic = "payments.settlement-replied.v1",
            Partition = new Partition(0),
            Offset = new Offset(0),
            Message = new Message<string, string> { Key = orderId.ToString("N"), Value = System.Text.Json.JsonSerializer.Serialize(reply), Headers = new Headers() }
        };
    }

    private sealed class NoOpCacheInvalidator : IOrderCacheInvalidator
    {
        public Task InvalidateAsync(Guid orderId, CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private sealed class NoOpBestsellersStore : IBestsellersStore
    {
        public Task RecordSaleAsync(string sku, string? categorySlug, int quantity, CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private sealed class NoOpCatalogClient : ICatalogClient
    {
        public Task<CatalogProductSnapshot?> FindBySkuAsync(string sku, CancellationToken cancellationToken) => Task.FromResult<CatalogProductSnapshot?>(null);
    }
}
