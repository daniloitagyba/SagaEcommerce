using BuildingBlocks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Orders.Infrastructure.Data;

namespace Orders.IntegrationTests;

/// <summary>Baseline coverage for the shared OutboxPublisher&lt;TDbContext&gt;. Exercises the two-phase claim-then-publish restructuring: a message must still end up published and marked processed exactly once now that the Kafka call happens outside the claiming transaction.</summary>
[Collection(PostgresCollectionDefinition.Name)]
public sealed class OutboxPublisherTests(PostgresFixture fixture) : IAsyncLifetime
{
    private string _connectionString = null!;
    private ServiceProvider _serviceProvider = null!;

    public async Task InitializeAsync()
    {
        _connectionString = await fixture.CreateSchemaAsync(nameof(OutboxPublisherTests));

        var services = new ServiceCollection();
        services.AddDbContext<OrdersDbContext>(options => options.UseNpgsql(_connectionString));
        _serviceProvider = services.BuildServiceProvider();

        await using var scope = _serviceProvider.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<OrdersDbContext>();
        await dbContext.Database.MigrateAsync();
    }

    public async Task DisposeAsync()
    {
        await _serviceProvider.DisposeAsync();
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
        services.AddDbContext<OrdersDbContext>(options => options.UseNpgsql(_connectionString));
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
