using System.Text;
using BuildingBlocks;
using Confluent.Kafka;
using Confluent.SchemaRegistry;
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

/// <summary>Closes the gap the audit's finding 1 flagged: before OrderStatusChanged, order_summaries and order_events only ever learned an order's status from OrderCreated/PaymentDecided, so a saga-driven Confirmed never reached either.</summary>
[Collection(PostgresCollectionDefinition.Name)]
public sealed class OrderStatusChangedProjectionTests(PostgresFixture fixture) : IAsyncLifetime, IDisposable
{
    private readonly RedpandaContainer _redpanda =
        new RedpandaBuilder("docker.redpanda.com/redpandadata/redpanda:v26.2.1").Build();

    private NpgsqlDataSource _dataSource = null!;
    private OrdersDbContext _dbContext = null!;
    private OrderStatusStore _orderStatusStore = null!;
    private OrderProjectionStore _projectionStore = null!;
    private InboxStore _inboxStore = null!;
    private OrderEventStoreAppender _eventStoreAppender = null!;
    private CachedSchemaRegistryClient _schemaRegistryClient = null!;

    public async Task InitializeAsync()
    {
        var connectionStringTask = fixture.CreateSchemaAsync(nameof(OrderStatusChangedProjectionTests));
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
        _orderStatusStore = new OrderStatusStore(
            _dataSource, new CouponRedemptionStore(), new PromotionCampaignStore(), new PaymentSettlementRequester(), new CustomerTierStore(), pipelineProvider);
        _projectionStore = new OrderProjectionStore(_dataSource, pipelineProvider);
        _inboxStore = new InboxStore(_dataSource, pipelineProvider);
        _eventStoreAppender = new OrderEventStoreAppender(_dataSource, pipelineProvider);

        _schemaRegistryClient = new CachedSchemaRegistryClient(new SchemaRegistryConfig { Url = _redpanda.GetSchemaRegistryAddress() });
    }

    public async Task DisposeAsync()
    {
        await _dbContext.DisposeAsync();
        await _dataSource.DisposeAsync();
        await _redpanda.DisposeAsync();
    }

    public void Dispose() => _schemaRegistryClient.Dispose();

    [Fact]
    public async Task AnOrchestratedConfirmationReachesBothTheReadModelAndTheEventStore()
    {
        var order = Order.Create("projection-test-customer", 199.90m, "BRL", DateTimeOffset.UtcNow);
        _dbContext.Orders.Add(order);
        await _dbContext.SaveChangesAsync();

        await _projectionStore.ProjectOrderCreatedAsync(
            order.Id, order.CustomerId, order.Amount, order.Currency, order.CreatedAt, DateTimeOffset.UtcNow, CancellationToken.None);

        var transitioned = await _orderStatusStore.TryConfirmAsync(order.Id, "projection-test-correlation", CancellationToken.None);
        Assert.True(transitioned);

        var payloadBytes = await ReadQueuedStatusChangedPayloadAsync();

        var projectionOptions = Options.Create(new OrderProjectionOptions());
        var processor = new OrderProjectionProcessor(
            _inboxStore, _projectionStore, _schemaRegistryClient, projectionOptions, NullLogger<OrderProjectionProcessor>.Instance);
        var projected = await processor.ProcessAsync(
            StatusChangedConsumeResult(order.Id, projectionOptions.Value.OrderStatusChangedTopic, payloadBytes),
            CancellationToken.None);
        Assert.Equal(MessageProcessingResult.Processed, projected);

        var eventStoreOptions = Options.Create(new OrderEventStoreOptions());
        var projector = new OrderEventStoreProjector(
            eventStoreOptions, _eventStoreAppender, _schemaRegistryClient, NullLogger<OrderEventStoreProjector>.Instance);
        await projector.AppendAsync(
            StatusChangedConsumeResult(order.Id, eventStoreOptions.Value.OrderStatusChangedTopic, payloadBytes),
            CancellationToken.None);

        var summary = await _dbContext.OrderSummaries.SingleAsync(s => s.OrderId == order.Id);
        Assert.Equal(OrderStatuses.Confirmed, summary.Status);

        var events = await _dbContext.OrderEvents.Where(e => e.OrderId == order.Id).ToListAsync();
        Assert.Contains(events, e => e.EventType == "OrderConfirmed");
    }

    /// <summary>Reads back the exact row OrderStatusStore queued so this test fails if the outbox payload shape ever drifts from what either projector expects to deserialize.</summary>
    private async Task<byte[]> ReadQueuedStatusChangedPayloadAsync()
    {
        await using var command = _dataSource.CreateCommand(
            "SELECT payload::text FROM outbox_messages WHERE event_type = @event_type ORDER BY occurred_at DESC LIMIT 1");
        command.Parameters.AddWithValue("event_type", nameof(OrderStatusChanged));
        var payload = (string)(await command.ExecuteScalarAsync())!;
        return Encoding.UTF8.GetBytes(payload);
    }

    private static ConsumeResult<string, byte[]> StatusChangedConsumeResult(Guid orderId, string topic, byte[] payload) =>
        new()
        {
            Topic = topic,
            Partition = new Partition(0),
            Offset = new Offset(0),
            Message = new Message<string, byte[]> { Key = orderId.ToString("N"), Value = payload, Headers = new Headers() }
        };
}
