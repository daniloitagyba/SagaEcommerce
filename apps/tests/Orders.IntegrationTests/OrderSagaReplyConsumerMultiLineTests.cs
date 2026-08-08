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
/// Milestone 78: a multi-line order now reserves every line, not just the
/// largest by value - these tests drive OrderSagaReplyConsumer's per-line
/// aggregation directly (the same testable seam OrderSagaReplyConsumerSettlementTests
/// uses), proving the two behaviours that only exist because there's more
/// than one line in flight: the saga waits for every line before advancing,
/// and an outright rejection on one line releases whichever siblings
/// already reserved instead of leaving them held forever.
/// </summary>
public sealed class OrderSagaReplyConsumerMultiLineTests : IAsyncLifetime, IDisposable
{
    private static readonly System.Text.Json.JsonSerializerOptions SerializerOptions = new(System.Text.Json.JsonSerializerDefaults.Web);

    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder("postgres:17-alpine")
        .WithDatabase("orders_test")
        .WithUsername("test_user")
        .WithPassword("test-password-not-a-secret")
        .Build();

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
        _sagaStore = new SagaOrchestrationStore(_dataSource, pipelineProvider);
    }

    public async Task DisposeAsync()
    {
        await _dbContext.DisposeAsync();
        await _dataSource.DisposeAsync();
        await Task.WhenAll(_postgres.DisposeAsync().AsTask(), _redpanda.DisposeAsync().AsTask());
    }

    public void Dispose() => _producer.Dispose();

    [Fact]
    public async Task TheSagaOnlyAdvancesToDecidePaymentOnceEveryLineHasReplied()
    {
        var (orderId, lineA, lineB) = await SeedTwoLineOrderAsync();
        var consumer = CreateConsumer();

        await consumer.DispatchAsync(ReservationReply(orderId, lineA, "SKU-A", reserved: true), CancellationToken.None);
        Assert.Equal(SagaStep.ReserveInventory, await CurrentSagaStepAsync(orderId));

        await consumer.DispatchAsync(ReservationReply(orderId, lineB, "SKU-B", reserved: true), CancellationToken.None);
        Assert.Equal(SagaStep.DecidePayment, await CurrentSagaStepAsync(orderId));
    }

    [Fact]
    public async Task AnOutrightRejectionOnOneLineReleasesTheSiblingThatAlreadyReservedAndCancelsTheOrder()
    {
        var (orderId, lineA, lineB) = await SeedTwoLineOrderAsync();
        var consumer = CreateConsumer();

        await consumer.DispatchAsync(ReservationReply(orderId, lineA, "SKU-A", reserved: true), CancellationToken.None);
        await consumer.DispatchAsync(ReservationReply(orderId, lineB, "SKU-B", reserved: false), CancellationToken.None);

        Assert.Equal(OrderStatuses.Cancelled, await CurrentOrderStatusAsync(orderId));
        Assert.Equal(0, await SagaRowCountAsync(orderId));

        using var releaseConsumer = new ConsumerBuilder<string, string>(new ConsumerConfig
        {
            BootstrapServers = _redpanda.GetBootstrapAddress(),
            GroupId = $"release-assert-{Guid.NewGuid():N}",
            AutoOffsetReset = AutoOffsetReset.Earliest
        }).Build();
        releaseConsumer.Subscribe(_options.ReleaseRequestedTopic);

        var published = releaseConsumer.Consume(TimeSpan.FromSeconds(15));
        Assert.NotNull(published);
        var release = System.Text.Json.JsonSerializer.Deserialize<InventoryReservationReleaseRequested>(published.Message.Value, SerializerOptions);
        Assert.Equal(lineA, release!.ReservationId);
        releaseConsumer.Close();
    }

    [Fact]
    public async Task CommitInventoryOnlyConfirmsOnceEveryLineHasCommittedAndHoldsIfOneFails()
    {
        var (orderId, lineA, lineB) = await SeedTwoLineOrderAtStepAsync(SagaStep.CommitInventory);
        var consumer = CreateConsumer();

        await consumer.DispatchAsync(CommitReply(orderId, lineA, "SKU-A", committed: true), CancellationToken.None);
        Assert.Equal(OrderStatuses.Created, await CurrentOrderStatusAsync(orderId));

        await consumer.DispatchAsync(CommitReply(orderId, lineB, "SKU-B", committed: false), CancellationToken.None);

        Assert.Equal(OrderStatuses.FulfillmentHold, await CurrentOrderStatusAsync(orderId));
    }

    private async Task<(Guid OrderId, Guid LineA, Guid LineB)> SeedTwoLineOrderAsync()
    {
        var order = Order.Create("multiline-test-customer", 249.80m, "BRL", DateTimeOffset.UtcNow);
        _dbContext.Orders.Add(order);
        await _dbContext.SaveChangesAsync();

        var lineA = new SagaReservationLine(Guid.NewGuid(), "SKU-A", 1);
        var lineB = new SagaReservationLine(Guid.NewGuid(), "SKU-B", 2);
        await _sagaStore.TrackReserveRequestedAsync(
            order.Id, "multiline-correlation", order.CustomerId, PaymentMethods.Pix, "01",
            [lineA, lineB], order.Amount, order.Currency, DateTimeOffset.UtcNow, CancellationToken.None);

        return (order.Id, lineA.ReservationId, lineB.ReservationId);
    }

    private async Task<(Guid OrderId, Guid LineA, Guid LineB)> SeedTwoLineOrderAtStepAsync(string step)
    {
        var (orderId, lineA, lineB) = await SeedTwoLineOrderAsync();
        var now = DateTimeOffset.UtcNow;
        await _sagaStore.TryAdvanceAsync(orderId, SagaStep.ReserveInventory, SagaStep.DecidePayment, now, CancellationToken.None);
        if (step == SagaStep.CommitInventory)
        {
            await _sagaStore.TryAdvanceAsync(orderId, SagaStep.DecidePayment, SagaStep.CommitInventory, now, CancellationToken.None);
        }
        else if (step == SagaStep.ReleaseInventory)
        {
            await _sagaStore.TryAdvanceAsync(orderId, SagaStep.DecidePayment, SagaStep.ReleaseInventory, now, CancellationToken.None);
        }

        return (orderId, lineA, lineB);
    }

    private async Task<string> CurrentSagaStepAsync(Guid orderId)
    {
        await using var command = _dataSource.CreateCommand("SELECT step FROM saga_orchestration_states WHERE order_id = @id");
        command.Parameters.AddWithValue("id", orderId);
        return (string)(await command.ExecuteScalarAsync())!;
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

    private OrderSagaReplyConsumer CreateConsumer() =>
        new(
            Options.Create(_options),
            _producer,
            _sagaStore,
            _orderStatusStore,
            new NoOpCacheInvalidator(),
            new NoOpBestsellersStore(),
            new NoOpCatalogClient(),
            NullLogger<OrderSagaReplyConsumer>.Instance);

    private static ConsumeResult<string, string> ReservationReply(Guid orderId, Guid reservationId, string sku, bool reserved)
    {
        var reply = new InventoryReservationReplied(reservationId, orderId, sku, 1, reserved, reserved ? null : "insufficient stock", "multiline-correlation", DateTimeOffset.UtcNow);
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
        var reply = new InventoryReservationCommitReplied(reservationId, orderId, sku, 1, committed, committed ? null : "commit failed", "multiline-correlation", DateTimeOffset.UtcNow);
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
