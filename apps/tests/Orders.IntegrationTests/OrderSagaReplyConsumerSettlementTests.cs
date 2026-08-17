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
using Testcontainers.Redpanda;

namespace Orders.IntegrationTests;

/// <summary>Reproduces the race this reconciliation exists to close: an authorization expires after the order has already shipped. Before this consumer existed, the order sat in Shipped forever, never charged, with no record it went wrong.</summary>
[Collection(PostgresCollectionDefinition.Name)]
public sealed class OrderSagaReplyConsumerSettlementTests(PostgresFixture fixture) : IAsyncLifetime, IDisposable
{
    private readonly RedpandaContainer _redpanda =
        new RedpandaBuilder("docker.redpanda.com/redpandadata/redpanda:v26.2.1").Build();

    private NpgsqlDataSource _dataSource = null!;
    private OrdersDbContext _dbContext = null!;
    private OrderStatusStore _orderStatusStore = null!;
    private IProducer<string, string> _producer = null!;

    public async Task InitializeAsync()
    {
        var connectionStringTask = fixture.CreateSchemaAsync(nameof(OrderSagaReplyConsumerSettlementTests));
        await Task.WhenAll(connectionStringTask, _redpanda.StartAsync());
        var connectionString = await connectionStringTask;

        var dbOptions = new DbContextOptionsBuilder<OrdersDbContext>().UseNpgsql(connectionString).Options;
        _dbContext = new OrdersDbContext(dbOptions);
        await _dbContext.Database.MigrateAsync();

        var pipelineProvider = new ServiceCollection()
            .AddOrdersResilience()
            .BuildServiceProvider()
            .GetRequiredService<ResiliencePipelineProvider<string>>();

        _dataSource = NpgsqlDataSource.Create(connectionString);
        _producer = new ProducerBuilder<string, string>(new ProducerConfig { BootstrapServers = _redpanda.GetBootstrapAddress() }).Build();

        _orderStatusStore = CreateOrderStatusStore(pipelineProvider);
    }

    public async Task DisposeAsync()
    {
        await _dbContext.DisposeAsync();
        await _dataSource.DisposeAsync();
        await _redpanda.DisposeAsync();
    }

    public void Dispose() => _producer.Dispose();

    private OrderStatusStore CreateOrderStatusStore(ResiliencePipelineProvider<string> pipelineProvider) =>
        new(_dataSource, new CouponRedemptionStore(), new PromotionCampaignStore(), new PaymentSettlementRequester(), new CustomerTierStore(), pipelineProvider);

    [Fact]
    public async Task AShippedOrderMovesToFulfillmentHoldWhenSettlementComesBackExpiredInsteadOfCaptured()
    {
        var orderId = await SeedShippedOrderAsync();
        var consumer = CreateConsumer();

        await consumer.DispatchAsync(SettlementReply(orderId, PaymentStates.Expired, requiresReconciliation: true), CancellationToken.None);

        var status = await CurrentStatusAsync(orderId);
        Assert.Equal(OrderStatuses.FulfillmentHold, status);
    }

    [Fact]
    public async Task AShippedOrderIsUntouchedWhenSettlementComesBackCaptured()
    {
        var orderId = await SeedShippedOrderAsync();
        var consumer = CreateConsumer();

        await consumer.DispatchAsync(SettlementReply(orderId, PaymentStates.Captured, requiresReconciliation: false), CancellationToken.None);

        var status = await CurrentStatusAsync(orderId);
        Assert.Equal(OrderStatuses.Shipped, status);
    }

    /// <summary>The gap finding 8 closed: before RequiresReconciliation existed, this consumer only recognized State == Expired by name, so a refund mismatch reply for any other state was silently dropped.</summary>
    [Fact]
    public async Task AShippedOrderMovesToFulfillmentHoldWhenARefundMismatchesAVoidedPayment()
    {
        var orderId = await SeedShippedOrderAsync();
        var consumer = CreateConsumer();

        await consumer.DispatchAsync(SettlementReply(orderId, PaymentStates.Voided, requiresReconciliation: true), CancellationToken.None);

        var status = await CurrentStatusAsync(orderId);
        Assert.Equal(OrderStatuses.FulfillmentHold, status);
    }

    private async Task<Guid> SeedShippedOrderAsync()
    {
        var order = Order.Create("settlement-test-customer", 149.90m, "BRL", DateTimeOffset.UtcNow);
        _dbContext.Orders.Add(order);
        await _dbContext.SaveChangesAsync();

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
            new SagaOrchestrationStore(_dataSource, new ServiceCollection().AddOrdersResilience().BuildServiceProvider().GetRequiredService<ResiliencePipelineProvider<string>>()),
            _orderStatusStore,
            new NoOpCacheInvalidator(),
            new NoOpBestsellersStore(),
            new NoOpCatalogClient(),
            TimeProvider.System,
            NullLogger<OrderSagaReplyConsumer>.Instance);

    private static ConsumeResult<string, string> SettlementReply(Guid orderId, string state, bool requiresReconciliation)
    {
        var reply = new PaymentSettlementReplied(orderId, Guid.NewGuid(), state, 149.90m, "BRL", "settlement-test-correlation", DateTimeOffset.UtcNow, requiresReconciliation);
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
