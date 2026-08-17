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

/// <summary>The Created/Backordered-origin operator-cancellation race: an order cancelled from outside the saga while it is still mid-flight. Proves OrderSagaReplyConsumer reacts to the cancellation flag instead of finishing as if nothing happened.</summary>
[Collection(PostgresCollectionDefinition.Name)]
public sealed class OrderSagaReplyConsumerCancellationRaceTests(PostgresFixture fixture) : IAsyncLifetime, IDisposable
{
    private static readonly System.Text.Json.JsonSerializerOptions SerializerOptions = new(System.Text.Json.JsonSerializerDefaults.Web);

    private readonly RedpandaContainer _redpanda =
        new RedpandaBuilder("docker.redpanda.com/redpandadata/redpanda:v26.2.1").Build();

    private readonly SagaOrchestrationOptions _options = new();
    private NpgsqlDataSource _dataSource = null!;
    private OrdersDbContext _dbContext = null!;
    private OrderStatusStore _orderStatusStore = null!;
    private SagaOrchestrationStore _sagaStore = null!;
    private IProducer<string, string> _producer = null!;

    public async Task InitializeAsync()
    {
        var connectionStringTask = fixture.CreateSchemaAsync(nameof(OrderSagaReplyConsumerCancellationRaceTests));
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

        _orderStatusStore = new OrderStatusStore(
            _dataSource,
            new CouponRedemptionStore(),
            new PromotionCampaignStore(),
            new PaymentSettlementRequester(),
            new CustomerTierStore(),
            pipelineProvider);
        _sagaStore = new SagaOrchestrationStore(_dataSource, pipelineProvider);
    }

    public async Task DisposeAsync()
    {
        await _dbContext.DisposeAsync();
        await _dataSource.DisposeAsync();
        await _redpanda.DisposeAsync();
    }

    public void Dispose() => _producer.Dispose();

    [Fact]
    public async Task ACancellationFlaggedWhileReservingReleasesEveryLineInsteadOfRequestingAPaymentDecision()
    {
        var (orderId, lineA, lineB) = await SeedTwoLineOrderAsync();
        await CancelFromOutsideTheSagaAsync(orderId);
        var consumer = CreateConsumer();

        await consumer.DispatchAsync(ReservationReply(orderId, lineA, "SKU-A", reserved: true), CancellationToken.None);
        await consumer.DispatchAsync(ReservationReply(orderId, lineB, "SKU-B", reserved: true), CancellationToken.None);

        Assert.Equal(0, await SagaRowCountAsync(orderId));

        var releasedIds = (await QueuedPayloadsAsync(orderId, _options.ReleaseRequestedTopic))
            .Select(payload => System.Text.Json.JsonSerializer.Deserialize<InventoryReservationReleaseRequested>(payload, SerializerOptions)!.ReservationId)
            .ToHashSet();

        Assert.True(releasedIds.SetEquals([lineA, lineB]));
    }

    [Fact]
    public async Task ACancellationFlaggedWhileCommittingRestocksWhatWasActuallyCommittedAndNeverConfirms()
    {
        var (orderId, lineA, lineB) = await SeedTwoLineOrderAtCommitStepAsync();
        await CancelFromOutsideTheSagaAsync(orderId);
        var consumer = CreateConsumer();

        await consumer.DispatchAsync(CommitReply(orderId, lineA, "SKU-A", committed: true), CancellationToken.None);
        await consumer.DispatchAsync(CommitReply(orderId, lineB, "SKU-B", committed: false), CancellationToken.None);

        Assert.Equal(OrderStatuses.Cancelled, await CurrentOrderStatusAsync(orderId));

        var queued = await QueuedPayloadsAsync(orderId, _options.RestockRequestedTopic);
        var restock = System.Text.Json.JsonSerializer.Deserialize<InventoryRestockRequested>(Assert.Single(queued), SerializerOptions);
        Assert.Equal("SKU-A", restock!.Sku);
    }

    private async Task<(Guid OrderId, Guid LineA, Guid LineB)> SeedTwoLineOrderAsync()
    {
        var order = Order.Create("cancel-race-test-customer", 249.80m, "BRL", DateTimeOffset.UtcNow);
        _dbContext.Orders.Add(order);
        await _dbContext.SaveChangesAsync();

        var lineA = new SagaReservationLine(Guid.NewGuid(), "SKU-A", 1);
        var lineB = new SagaReservationLine(Guid.NewGuid(), "SKU-B", 2);
        await _sagaStore.TrackReserveRequestedAsync(
            order.Id, "cancel-race-correlation", order.CustomerId, PaymentMethods.Pix, "01",
            [lineA, lineB], order.Amount, order.Currency, DateTimeOffset.UtcNow, CancellationToken.None);

        return (order.Id, lineA.ReservationId, lineB.ReservationId);
    }

    private async Task<(Guid OrderId, Guid LineA, Guid LineB)> SeedTwoLineOrderAtCommitStepAsync()
    {
        var (orderId, lineA, lineB) = await SeedTwoLineOrderAsync();
        var now = DateTimeOffset.UtcNow;
        await _sagaStore.TryAdvanceAsync(orderId, SagaStep.ReserveInventory, SagaStep.DecidePayment, now, CancellationToken.None);
        await _sagaStore.TryAdvanceAsync(orderId, SagaStep.DecidePayment, SagaStep.CommitInventory, now, CancellationToken.None);
        return (orderId, lineA, lineB);
    }

    /// <summary>The same two writes EfOrderStatusRepository.TryTransitionAsync makes when an operator or shopper cancels an order from Created.</summary>
    private async Task CancelFromOutsideTheSagaAsync(Guid orderId)
    {
        await using (var command = _dataSource.CreateCommand("UPDATE orders SET status = 'Cancelled' WHERE id = @id"))
        {
            command.Parameters.AddWithValue("id", orderId);
            await command.ExecuteNonQueryAsync();
        }

        await using (var command = _dataSource.CreateCommand(
            "UPDATE saga_orchestration_states SET cancellation_requested_at = COALESCE(cancellation_requested_at, now()) WHERE order_id = @id"))
        {
            command.Parameters.AddWithValue("id", orderId);
            await command.ExecuteNonQueryAsync();
        }
    }

    private async Task<int> SagaRowCountAsync(Guid orderId)
    {
        await using var command = _dataSource.CreateCommand("SELECT COUNT(*) FROM saga_orchestration_states WHERE order_id = @id");
        command.Parameters.AddWithValue("id", orderId);
        return (int)(long)(await command.ExecuteScalarAsync())!;
    }

    private async Task<string> CurrentOrderStatusAsync(Guid orderId)
    {
        await using var command = _dataSource.CreateCommand("SELECT status FROM orders WHERE id = @id");
        command.Parameters.AddWithValue("id", orderId);
        return (string)(await command.ExecuteScalarAsync())!;
    }

    private async Task<IReadOnlyList<string>> QueuedPayloadsAsync(Guid orderId, string topic)
    {
        var payloads = new List<string>();
        await using var command = _dataSource.CreateCommand(
            "SELECT payload::text FROM saga_outbox_messages WHERE order_id = @order_id AND topic = @topic ORDER BY occurred_at");
        command.Parameters.AddWithValue("order_id", orderId);
        command.Parameters.AddWithValue("topic", topic);
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            payloads.Add(reader.GetString(0));
        }

        return payloads;
    }

    private OrderSagaReplyConsumer CreateConsumer() =>
        new(
            Options.Create(_options),
            _sagaStore,
            _orderStatusStore,
            new NoOpCacheInvalidator(),
            new NoOpBestsellersStore(),
            new NoOpCatalogClient(),
            TimeProvider.System,
            NullLogger<OrderSagaReplyConsumer>.Instance);

    private static ConsumeResult<string, string> ReservationReply(Guid orderId, Guid reservationId, string sku, bool reserved)
    {
        var reply = new InventoryReservationReplied(reservationId, orderId, sku, 1, reserved, reserved ? null : "insufficient stock", "cancel-race-correlation", DateTimeOffset.UtcNow);
        return new ConsumeResult<string, string>
        {
            Topic = "inventory.reservation-replied.v1",
            Partition = new Partition(0),
            Offset = new Offset(0),
            Message = new Message<string, string> { Key = sku, Value = System.Text.Json.JsonSerializer.Serialize(reply, SerializerOptions), Headers = new Headers() }
        };
    }

    private static ConsumeResult<string, string> CommitReply(Guid orderId, Guid reservationId, string sku, bool committed)
    {
        var reply = new InventoryReservationCommitReplied(reservationId, orderId, sku, 1, committed, committed ? null : "commit failed", "cancel-race-correlation", DateTimeOffset.UtcNow);
        return new ConsumeResult<string, string>
        {
            Topic = "inventory.reservation-commit-replied.v1",
            Partition = new Partition(0),
            Offset = new Offset(0),
            Message = new Message<string, string> { Key = sku, Value = System.Text.Json.JsonSerializer.Serialize(reply, SerializerOptions), Headers = new Headers() }
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
