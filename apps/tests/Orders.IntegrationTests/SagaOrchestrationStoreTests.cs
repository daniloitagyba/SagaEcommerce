using BuildingBlocks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Orders.Infrastructure.Data;
using Orders.Worker;
using Polly.Registry;
using Testcontainers.PostgreSql;

namespace Orders.IntegrationTests;

public sealed class SagaOrchestrationStoreTests : IAsyncLifetime
{
    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder("postgres:17-alpine")
        .WithDatabase("orders_test")
        .WithUsername("test_user")
        .WithPassword("test-password-not-a-secret")
        .Build();

    private NpgsqlDataSource? _dataSource;
    private SagaOrchestrationStore? _store;

    public async Task InitializeAsync()
    {
        await _postgres.StartAsync();

        var options = new DbContextOptionsBuilder<OrdersDbContext>()
            .UseNpgsql(_postgres.GetConnectionString())
            .Options;
        await using (var context = new OrdersDbContext(options))
        {
            await context.Database.MigrateAsync();
        }

        var pipelineProvider = new ServiceCollection()
            .AddOrdersResilience()
            .BuildServiceProvider()
            .GetRequiredService<ResiliencePipelineProvider<string>>();

        _dataSource = NpgsqlDataSource.Create(_postgres.GetConnectionString());
        _store = new SagaOrchestrationStore(_dataSource, pipelineProvider);
    }

    public async Task DisposeAsync()
    {
        if (_dataSource is not null)
        {
            await _dataSource.DisposeAsync();
        }

        await _postgres.DisposeAsync();
    }

    [Fact]
    public async Task TrackedSagaAdvancesThroughStepsAndDisappearsOnCompletion()
    {
        var orderId = Guid.NewGuid();
        var reservationId = Guid.NewGuid();
        var requestedAt = DateTimeOffset.UtcNow;

        await _store!.TrackReserveRequestedAsync(orderId, "correlation-1", "customer-1", "Pix", "01", reservationId, "SKU-1", 2, 49.90m, "BRL", requestedAt, CancellationToken.None);
        var advanced = await _store.TryAdvanceAsync(orderId, SagaStep.ReserveInventory, SagaStep.DecidePayment, requestedAt.AddSeconds(1), CancellationToken.None);
        var completed = await _store.TryCompleteAsync(orderId, SagaStep.DecidePayment, CancellationToken.None);
        var secondAttempt = await _store.TryCompleteAsync(orderId, SagaStep.DecidePayment, CancellationToken.None);

        Assert.NotNull(advanced);
        Assert.Equal(SagaStep.DecidePayment, advanced!.Step);
        Assert.Equal("correlation-1", advanced.CorrelationId);
        Assert.Equal(reservationId, advanced.ReservationId);
        Assert.Equal("SKU-1", advanced.Sku);
        Assert.Equal(2, advanced.Quantity);

        Assert.NotNull(completed);
        Assert.Equal(SagaStep.DecidePayment, completed!.Step);
        Assert.Null(secondAttempt);
    }

    [Fact]
    public async Task TryAdvanceAsyncIsANoOpWhenTheCurrentStepDoesNotMatch()
    {
        var orderId = Guid.NewGuid();
        var requestedAt = DateTimeOffset.UtcNow;

        await _store!.TrackReserveRequestedAsync(orderId, "correlation-1", "customer-1", "Pix", "01", Guid.NewGuid(), "SKU-1", 1, 49.90m, "BRL", requestedAt, CancellationToken.None);

        // A stale/duplicate reply for a step the saga has already moved
        // past (e.g. a redelivered Reserve reply arriving after the saga
        // already advanced to DecidePayment) must not corrupt state.
        var staleAdvance = await _store.TryAdvanceAsync(orderId, SagaStep.CommitInventory, SagaStep.DecidePayment, requestedAt, CancellationToken.None);
        var realAdvance = await _store.TryAdvanceAsync(orderId, SagaStep.ReserveInventory, SagaStep.DecidePayment, requestedAt, CancellationToken.None);

        Assert.Null(staleAdvance);
        Assert.NotNull(realAdvance);
    }

    [Fact]
    public async Task TryCompleteAsyncReturnsNullForAnUntrackedOrder()
    {
        var result = await _store!.TryCompleteAsync(Guid.NewGuid(), SagaStep.CommitInventory, CancellationToken.None);

        Assert.Null(result);
    }

    [Fact]
    public async Task ClaimTimedOutAsyncOnlyClaimsSagasPastTheCutoffAndRemovesThem()
    {
        var timeout = TimeSpan.FromMinutes(5);
        var now = DateTimeOffset.UtcNow;
        var staleOrderId = Guid.NewGuid();
        var freshOrderId = Guid.NewGuid();

        await _store!.TrackReserveRequestedAsync(staleOrderId, "stale-correlation", "customer-stale", "Pix", "01", Guid.NewGuid(), "SKU-1", 1, 49.90m, "BRL", now - timeout - TimeSpan.FromSeconds(1), CancellationToken.None);
        await _store.TrackReserveRequestedAsync(freshOrderId, "fresh-correlation", "customer-fresh", "Pix", "01", Guid.NewGuid(), "SKU-1", 1, 49.90m, "BRL", now, CancellationToken.None);

        var firstClaim = await _store.ClaimTimedOutAsync(timeout, now, batchSize: 100, CancellationToken.None);
        var secondClaim = await _store.ClaimTimedOutAsync(timeout, now, batchSize: 100, CancellationToken.None);
        var freshStillPending = await _store.TryCompleteAsync(freshOrderId, SagaStep.ReserveInventory, CancellationToken.None);

        Assert.Single(firstClaim);
        Assert.Equal(staleOrderId, firstClaim[0].OrderId);
        Assert.Equal("stale-correlation", firstClaim[0].Saga.CorrelationId);
        Assert.Empty(secondClaim);
        Assert.NotNull(freshStillPending);
    }
}
