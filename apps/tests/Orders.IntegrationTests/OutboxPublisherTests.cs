using BuildingBlocks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Orders.Infrastructure.Data;
using Testcontainers.PostgreSql;

namespace Orders.IntegrationTests;

/// <summary>
/// Baseline coverage for the shared OutboxPublisher&lt;TDbContext&gt; - it had
/// none before, despite every service depending on it. Exercises the
/// two-phase claim-then-publish restructuring directly: a message must
/// still end up published and marked processed exactly once, now that the
/// Kafka call happens outside the transaction that claims it (see
/// OutboxPublisher.ClaimBatchAsync's own comment for why that split exists).
/// </summary>
public sealed class OutboxPublisherTests : IAsyncLifetime
{
    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder("postgres:17-alpine")
        .WithDatabase("orders_test")
        .WithUsername("test_user")
        .WithPassword("test-password-not-a-secret")
        .Build();

    private ServiceProvider _serviceProvider = null!;

    public async Task InitializeAsync()
    {
        await _postgres.StartAsync();

        var services = new ServiceCollection();
        services.AddDbContext<OrdersDbContext>(options => options.UseNpgsql(_postgres.GetConnectionString()));
        _serviceProvider = services.BuildServiceProvider();

        await using var scope = _serviceProvider.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<OrdersDbContext>();
        await dbContext.Database.MigrateAsync();
    }

    public async Task DisposeAsync()
    {
        await _serviceProvider.DisposeAsync();
        await _postgres.DisposeAsync();
    }

    [Fact]
    public async Task APendingMessageIsPublishedExactlyOnceAndMarkedProcessed()
    {
        var eventId = Guid.NewGuid();
        await using (var scope = _serviceProvider.CreateAsyncScope())
        {
            var dbContext = scope.ServiceProvider.GetRequiredService<OrdersDbContext>();
            dbContext.OutboxMessages.Add(OutboxMessage.Create(
                eventId, "TestEvent", "{}", DateTimeOffset.UtcNow, "outbox-test-correlation", null, null));
            await dbContext.SaveChangesAsync();
        }

        var dispatcher = new RecordingDispatcher();
        var publisher = CreatePublisher(dispatcher);

        var published = await publisher.ProcessBatchAsync(CancellationToken.None);

        Assert.Equal(1, published);
        Assert.Equal([eventId], dispatcher.PublishedEventIds);

        await using var assertScope = _serviceProvider.CreateAsyncScope();
        var assertDbContext = assertScope.ServiceProvider.GetRequiredService<OrdersDbContext>();
        var message = await assertDbContext.OutboxMessages.SingleAsync(m => m.Id == eventId);
        Assert.NotNull(message.ProcessedAt);

        // A second pass must not re-publish - ProcessedAt already excludes it from the claim query.
        var secondPublished = await publisher.ProcessBatchAsync(CancellationToken.None);
        Assert.Equal(0, secondPublished);
        Assert.Single(dispatcher.PublishedEventIds);
    }

    [Fact]
    public async Task AFailingDispatchLeavesTheMessageUnprocessedForRetry()
    {
        var eventId = Guid.NewGuid();
        await using (var scope = _serviceProvider.CreateAsyncScope())
        {
            var dbContext = scope.ServiceProvider.GetRequiredService<OrdersDbContext>();
            dbContext.OutboxMessages.Add(OutboxMessage.Create(
                eventId, "TestEvent", "{}", DateTimeOffset.UtcNow, "outbox-test-correlation", null, null));
            await dbContext.SaveChangesAsync();
        }

        var dispatcher = new RecordingDispatcher { ThrowOnPublish = true };
        var publisher = CreatePublisher(dispatcher);

        var published = await publisher.ProcessBatchAsync(CancellationToken.None);

        // The claim still counts as "processed this tick" from the loop's
        // perspective (a batch was found and acted on) - what matters here
        // is the row's own state, checked below.
        Assert.Equal(1, published);

        await using var assertScope = _serviceProvider.CreateAsyncScope();
        var assertDbContext = assertScope.ServiceProvider.GetRequiredService<OrdersDbContext>();
        var message = await assertDbContext.OutboxMessages.SingleAsync(m => m.Id == eventId);
        Assert.Null(message.ProcessedAt);
        Assert.Equal(1, message.AttemptCount);
        Assert.NotNull(message.LastError);
    }

    private OutboxPublisher<OrdersDbContext> CreatePublisher(IOutboxEventDispatcher dispatcher)
    {
        var services = new ServiceCollection();
        services.AddDbContext<OrdersDbContext>(options => options.UseNpgsql(_postgres.GetConnectionString()));
        services.AddScoped<IOutboxEventDispatcher>(_ => dispatcher);
        var scopedProvider = services.BuildServiceProvider();

        return new OutboxPublisher<OrdersDbContext>(
            scopedProvider.GetRequiredService<IServiceScopeFactory>(),
            Options.Create(new OutboxOptions { ClaimWindowSeconds = 30, PendingSampleIntervalSeconds = 0 }),
            new ConfigurationBuilder().Build(),
            NullLogger<OutboxPublisher<OrdersDbContext>>.Instance);
    }

    private sealed class RecordingDispatcher : IOutboxEventDispatcher
    {
        public bool ThrowOnPublish { get; init; }

        public List<Guid> PublishedEventIds { get; } = [];

        public Task<IReadOnlyDictionary<string, object?>> PublishAsync(OutboxMessage message, CancellationToken cancellationToken)
        {
            if (ThrowOnPublish)
            {
                throw new InvalidOperationException("Simulated dispatch failure.");
            }

            PublishedEventIds.Add(message.Id);
            return Task.FromResult<IReadOnlyDictionary<string, object?>>(new Dictionary<string, object?>());
        }
    }
}
