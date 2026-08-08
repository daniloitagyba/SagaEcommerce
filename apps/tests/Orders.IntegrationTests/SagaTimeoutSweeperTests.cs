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
/// Milestone 77: proves each of SagaTimeoutSweeper.ResolveAsync's four
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

        var couponStore = new CouponRedemptionStore(_dataSource, pipelineProvider, NullLogger<CouponRedemptionStore>.Instance);
        var settlementRequester = new PaymentSettlementRequester(_producer, Options.Create(new PaymentSettlementRequestOptions()), NullLogger<PaymentSettlementRequester>.Instance);
        var customerTierStore = new CustomerTierStore(_dataSource, pipelineProvider, NullLogger<CustomerTierStore>.Instance);
        _orderStatusStore = new OrderStatusStore(_dataSource, couponStore, settlementRequester, customerTierStore, pipelineProvider);
        _sagaStore = new SagaOrchestrationStore(_dataSource, pipelineProvider);

        var leaderElection = new LeaderElectionService(
            Options.Create(new LeaderElectionOptions()), new ConfigurationBuilder().Build(), NullLogger<LeaderElectionService>.Instance);

        _sweeper = new SagaTimeoutSweeper(
            Options.Create(_options), _producer, _sagaStore, _orderStatusStore, leaderElection, NullLogger<SagaTimeoutSweeper>.Instance);
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

        await _sweeper.ResolveAsync(orderId, saga, CancellationToken.None);

        Assert.Equal(OrderStatuses.Cancelled, await CurrentStatusAsync(orderId));
        AssertReleasePublished(saga.ReservationId);
    }

    [Fact]
    public async Task ATimeoutAtReleaseInventoryResendsTheReleaseAndCancelsTheOrder()
    {
        var (orderId, saga) = await SeedAsync(SagaStep.ReleaseInventory);

        await _sweeper.ResolveAsync(orderId, saga, CancellationToken.None);

        Assert.Equal(OrderStatuses.Cancelled, await CurrentStatusAsync(orderId));
        AssertReleasePublished(saga.ReservationId);
    }

    [Fact]
    public async Task ATimeoutAtCommitInventoryConfirmsThenMovesToFulfillmentHoldWithoutReleasing()
    {
        var (orderId, saga) = await SeedAsync(SagaStep.CommitInventory);

        await _sweeper.ResolveAsync(orderId, saga, CancellationToken.None);

        Assert.Equal(OrderStatuses.FulfillmentHold, await CurrentStatusAsync(orderId));
        AssertNoReleasePublished();
    }

    [Fact]
    public async Task ATimeoutAtReserveInventoryOnlyCancelsBecauseWhetherAnythingWasReservedIsUnknown()
    {
        var (orderId, saga) = await SeedAsync(SagaStep.ReserveInventory);

        await _sweeper.ResolveAsync(orderId, saga, CancellationToken.None);

        Assert.Equal(OrderStatuses.Cancelled, await CurrentStatusAsync(orderId));
        AssertNoReleasePublished();
    }

    private async Task<(Guid OrderId, SagaOrchestrationRecord Saga)> SeedAsync(string step)
    {
        var order = Order.Create("saga-timeout-test-customer", 199.90m, "BRL", DateTimeOffset.UtcNow);
        _dbContext.Orders.Add(order);
        await _dbContext.SaveChangesAsync();

        var reservationId = Guid.NewGuid();
        var requestedAt = DateTimeOffset.UtcNow.AddMinutes(-10);
        await _sagaStore.TrackReserveRequestedAsync(
            order.Id, "saga-timeout-correlation", order.CustomerId, PaymentMethods.Card, "01",
            reservationId, "SKU-TIMEOUT-001", 3, order.Amount, order.Currency, requestedAt, CancellationToken.None);

        var record = step switch
        {
            SagaStep.ReserveInventory => new SagaOrchestrationRecord(
                "saga-timeout-correlation", order.CustomerId, PaymentMethods.Card, "01", requestedAt,
                SagaStep.ReserveInventory, reservationId, "SKU-TIMEOUT-001", 3, order.Amount, order.Currency),
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

    private void AssertReleasePublished(Guid reservationId)
    {
        using var consumer = new ConsumerBuilder<string, string>(new ConsumerConfig
        {
            BootstrapServers = _redpanda.GetBootstrapAddress(),
            GroupId = $"release-assert-{Guid.NewGuid():N}",
            AutoOffsetReset = AutoOffsetReset.Earliest
        }).Build();
        consumer.Subscribe(_options.ReleaseRequestedTopic);

        var result = consumer.Consume(TimeSpan.FromSeconds(15));
        Assert.NotNull(result);
        var released = System.Text.Json.JsonSerializer.Deserialize<InventoryReservationReleaseRequested>(result.Message.Value, SerializerOptions);
        Assert.Equal(reservationId, released!.ReservationId);
        consumer.Close();
    }

    private void AssertNoReleasePublished()
    {
        using var consumer = new ConsumerBuilder<string, string>(new ConsumerConfig
        {
            BootstrapServers = _redpanda.GetBootstrapAddress(),
            GroupId = $"release-assert-{Guid.NewGuid():N}",
            AutoOffsetReset = AutoOffsetReset.Earliest
        }).Build();
        consumer.Subscribe(_options.ReleaseRequestedTopic);

        try
        {
            // Nothing in this test ever produces to this topic, so on a
            // fresh broker it may not exist yet at all - that is itself
            // proof nothing was released, not a failure to distinguish
            // from an empty-but-existing topic.
            var result = consumer.Consume(TimeSpan.FromSeconds(3));
            Assert.Null(result);
        }
        catch (ConsumeException exception) when (exception.Error.Code == ErrorCode.UnknownTopicOrPart)
        {
        }
        finally
        {
            consumer.Close();
        }
    }
}
