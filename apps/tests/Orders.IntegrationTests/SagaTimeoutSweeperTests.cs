using BuildingBlocks;
using Confluent.Kafka;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
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
/// Proves each of SagaTimeoutSweeper.ResolveAsync's four
/// step-dependent branches does what the class comment claims - a release
/// published exactly for the two steps where a reservation is certain to
/// exist and uncommitted, FulfillmentHold (not a guess) when the commit
/// itself is the unknown, and no release attempted for the one step that's
/// still an open gap.
/// </summary>
public sealed class SagaTimeoutSweeperTests : IAsyncLifetime, IDisposable
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
    private SagaTimeoutSweeper _sweeper = null!;

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

        _orderStatusStore = new OrderStatusStore(
            _dataSource,
            new CouponRedemptionStore(),
            new PaymentSettlementRequester(),
            new CustomerTierStore(),
            pipelineProvider);
        _sagaStore = new SagaOrchestrationStore(_dataSource, pipelineProvider);

        var leaderElection = new LeaderElectionService(
            Options.Create(new LeaderElectionOptions()), new ConfigurationBuilder().Build(), NullLogger<LeaderElectionService>.Instance);

        _sweeper = new SagaTimeoutSweeper(
            Options.Create(_options), _sagaStore, _orderStatusStore, leaderElection, TimeProvider.System, NullLogger<SagaTimeoutSweeper>.Instance);
    }

    public async Task DisposeAsync()
    {
        await _dbContext.DisposeAsync();
        await _dataSource.DisposeAsync();
        await Task.WhenAll(_postgres.DisposeAsync().AsTask(), _redpanda.DisposeAsync().AsTask());
    }

    public void Dispose() => _producer.Dispose();

    [Fact]
    public async Task ATimeoutAtDecidePaymentReleasesTheReservationAndCancelsTheOrder()
    {
        var (orderId, saga) = await SeedAsync(SagaStep.DecidePayment);

        Assert.Equal(1, await _sweeper.SweepOnceAsync(TimeSpan.FromMinutes(1), DateTimeOffset.UtcNow, CancellationToken.None));

        Assert.Equal(OrderStatuses.Cancelled, await CurrentStatusAsync(orderId));
        await AssertReleaseQueuedAsync(saga.Lines[0].ReservationId);
    }

    [Fact]
    public async Task ATimeoutAtReleaseInventoryResendsTheReleaseAndCancelsTheOrder()
    {
        var (orderId, saga) = await SeedAsync(SagaStep.ReleaseInventory);

        Assert.Equal(1, await _sweeper.SweepOnceAsync(TimeSpan.FromMinutes(1), DateTimeOffset.UtcNow, CancellationToken.None));

        Assert.Equal(OrderStatuses.Cancelled, await CurrentStatusAsync(orderId));
        await AssertReleaseQueuedAsync(saga.Lines[0].ReservationId);
    }

    [Fact]
    public async Task ATimeoutAtCommitInventoryConfirmsThenMovesToFulfillmentHoldWithoutReleasing()
    {
        var (orderId, saga) = await SeedAsync(SagaStep.CommitInventory);

        Assert.Equal(1, await _sweeper.SweepOnceAsync(TimeSpan.FromMinutes(1), DateTimeOffset.UtcNow, CancellationToken.None));

        Assert.Equal(OrderStatuses.FulfillmentHold, await CurrentStatusAsync(orderId));
        Assert.Equal(0, await QueuedReleaseCountAsync(orderId));
    }

    [Fact]
    public async Task ATimeoutAtReserveInventoryQueuesASafeReleaseInCaseTheReplyWasLost()
    {
        var (orderId, saga) = await SeedAsync(SagaStep.ReserveInventory);

        Assert.Equal(1, await _sweeper.SweepOnceAsync(TimeSpan.FromMinutes(1), DateTimeOffset.UtcNow, CancellationToken.None));

        Assert.Equal(OrderStatuses.Cancelled, await CurrentStatusAsync(orderId));
        await AssertReleaseQueuedAsync(saga.Lines[0].ReservationId);
    }

    /// <summary>
    /// Reproduces the bug this fix closes: before ParkedAt existed, this
    /// sweeper claimed a backordered order exactly like the un-parked case
    /// above (SagaOrchestration:TimeoutSeconds is 5, far shorter than
    /// Backorder:TimeoutMinutes' 120), cancelled it, and deleted the saga
    /// row - so a restock arriving afterwards reserved against a
    /// reservationId nothing was tracking any more, leaking that stock
    /// forever. A parked row must survive this sweep untouched; only
    /// BackorderTimeoutSweeper's own, much longer window may resolve it.
    /// </summary>
    [Fact]
    public async Task ATimeoutAtReserveInventoryDoesNotCancelAParkedBackorderedOrder()
    {
        var (orderId, _) = await SeedAsync(SagaStep.ReserveInventory);
        await _orderStatusStore.TryTransitionAsync(orderId, OrderStatuses.Backordered, "saga-timeout-correlation", CancellationToken.None);
        await _sagaStore.MarkParkedAsync(orderId, DateTimeOffset.UtcNow.AddMinutes(-10), CancellationToken.None);

        Assert.Equal(0, await _sweeper.SweepOnceAsync(TimeSpan.FromMinutes(1), DateTimeOffset.UtcNow, CancellationToken.None));

        Assert.Equal(OrderStatuses.Backordered, await CurrentStatusAsync(orderId));
        Assert.Equal(0, await QueuedReleaseCountAsync(orderId));
    }

    private async Task<(Guid OrderId, SagaOrchestrationRecord Saga)> SeedAsync(string step)
    {
        var order = Order.Create("saga-timeout-test-customer", 199.90m, "BRL", DateTimeOffset.UtcNow);
        _dbContext.Orders.Add(order);
        await _dbContext.SaveChangesAsync();

        var reservationId = Guid.NewGuid();
        var requestedAt = DateTimeOffset.UtcNow.AddMinutes(-10);
        var line = new SagaReservationLine(reservationId, "SKU-TIMEOUT-001", 3);
        await _sagaStore.TrackReserveRequestedAsync(
            order.Id, "saga-timeout-correlation", order.CustomerId, PaymentMethods.Card, "01",
            [line], order.Amount, order.Currency, requestedAt, CancellationToken.None);

        var record = step switch
        {
            SagaStep.ReserveInventory => new SagaOrchestrationRecord(
                "saga-timeout-correlation", order.CustomerId, PaymentMethods.Card, "01", requestedAt,
                SagaStep.ReserveInventory, order.Amount, order.Currency,
                [new SagaLineRecord(0, reservationId, "SKU-TIMEOUT-001", 3, Reserved: null, Committed: null, Released: null)]),
            _ => await _sagaStore.TryAdvanceAsync(order.Id, SagaStep.ReserveInventory, step, requestedAt, CancellationToken.None)
                 ?? throw new InvalidOperationException($"Failed to advance seeded saga row to {step}")
        };

        return (order.Id, record);
    }

    private async Task<string> CurrentStatusAsync(Guid orderId)
    {
        await using var command = _dataSource.CreateCommand("SELECT status FROM orders WHERE id = @id");
        command.Parameters.AddWithValue("id", orderId);
        return (string)(await command.ExecuteScalarAsync())!;
    }

    private async Task AssertReleaseQueuedAsync(Guid reservationId)
    {
        await using var command = _dataSource.CreateCommand(
            "SELECT payload::text FROM saga_outbox_messages WHERE topic = @topic ORDER BY occurred_at LIMIT 1");
        command.Parameters.AddWithValue("topic", _options.ReleaseRequestedTopic);
        var payload = (string?)await command.ExecuteScalarAsync();
        Assert.NotNull(payload);
        var released = System.Text.Json.JsonSerializer.Deserialize<InventoryReservationReleaseRequested>(payload, SerializerOptions);
        Assert.Equal(reservationId, released!.ReservationId);
    }

    private async Task<long> QueuedReleaseCountAsync(Guid orderId)
    {
        await using var command = _dataSource.CreateCommand(
            "SELECT COUNT(*) FROM saga_outbox_messages WHERE order_id = @order_id AND topic = @topic");
        command.Parameters.AddWithValue("order_id", orderId);
        command.Parameters.AddWithValue("topic", _options.ReleaseRequestedTopic);
        return (long)(await command.ExecuteScalarAsync())!;
    }
}
