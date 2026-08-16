using BuildingBlocks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Orders.Domain;
using Orders.Infrastructure.Data;
using Orders.Worker;
using Polly.Registry;

namespace Orders.IntegrationTests;

[Collection(PostgresCollectionDefinition.Name)]
public sealed class OrdersDbContextTests(PostgresFixture fixture) : IAsyncLifetime
{
    private string _connectionString = null!;

    public async Task InitializeAsync()
    {
        _connectionString = await fixture.CreateSchemaAsync(nameof(OrdersDbContextTests));
    }

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task MigrationsSupportAtomicOutboxAndIdempotentInbox()
    {
        var options = new DbContextOptionsBuilder<OrdersDbContext>()
            .UseNpgsql(_connectionString)
            .Options;

        var expected = Order.Create("integration-customer", 49.90m, "BRL", DateTimeOffset.UtcNow);
        var outboxMessage = OutboxMessage.Create(
            Guid.NewGuid(),
            "OrderCreated",
            "{\"orderId\":\"integration-test\"}",
            expected.CreatedAt,
            "integration-correlation",
            null,
            null);

        await using (var writeContext = new OrdersDbContext(options))
        {
            await writeContext.Database.MigrateAsync(CancellationToken.None);
            await using var transaction = await writeContext.Database.BeginTransactionAsync(CancellationToken.None);
            writeContext.Orders.Add(expected);
            writeContext.OutboxMessages.Add(outboxMessage);
            await writeContext.SaveChangesAsync(CancellationToken.None);
            await transaction.CommitAsync(CancellationToken.None);
        }

        var pipelineProvider = new ServiceCollection()
            .AddOrdersResilience()
            .BuildServiceProvider()
            .GetRequiredService<ResiliencePipelineProvider<string>>();

        await using var dataSource = NpgsqlDataSource.Create(_connectionString);
        var inboxStore = new InboxStore(dataSource, pipelineProvider);
        var eventId = Guid.NewGuid();
        var firstProcessing = await inboxStore.TryRecordAsync(
            "orders-worker",
            eventId,
            "orders.created.v1",
            1,
            42,
            "integration-correlation",
            DateTimeOffset.UtcNow,
            CancellationToken.None);
        var duplicateProcessing = await inboxStore.TryRecordAsync(
            "orders-worker",
            eventId,
            "orders.created.v1",
            1,
            42,
            "integration-correlation",
            DateTimeOffset.UtcNow,
            CancellationToken.None);
        var reusedPositionProcessing = await inboxStore.TryRecordAsync(
            "orders-worker",
            Guid.NewGuid(),
            "orders.created.v1",
            1,
            42,
            "integration-correlation",
            DateTimeOffset.UtcNow,
            CancellationToken.None);
        var differentConsumerProcessing = await inboxStore.TryRecordAsync(
            "audit-worker",
            eventId,
            "orders.created.v1",
            1,
            42,
            "integration-correlation",
            DateTimeOffset.UtcNow,
            CancellationToken.None);

        await using var readContext = new OrdersDbContext(options);
        var actual = await readContext.Orders
            .AsNoTracking()
            .SingleAsync(order => order.Id == expected.Id, CancellationToken.None);
        var actualMessage = await readContext.OutboxMessages
            .AsNoTracking()
            .SingleAsync(message => message.Id == outboxMessage.Id, CancellationToken.None);
        var inboxEntries = await readContext.InboxMessages
            .AsNoTracking()
            .Where(message => message.EventId == eventId)
            .ToListAsync(CancellationToken.None);
        var totalInboxEntries = await readContext.InboxMessages
            .AsNoTracking()
            .CountAsync(CancellationToken.None);

        Assert.Equal(expected.CustomerId, actual.CustomerId);
        Assert.Equal(expected.Amount, actual.Amount);
        Assert.Equal(expected.Currency, actual.Currency);
        Assert.Equal("Created", actual.Status);
        Assert.Equal(expected.CreatedAt.ToUnixTimeMilliseconds(), actualMessage.OccurredAt.ToUnixTimeMilliseconds());
        Assert.Equal("integration-correlation", actualMessage.CorrelationId);
        Assert.Null(actualMessage.ProcessedAt);
        Assert.True(firstProcessing);
        Assert.False(duplicateProcessing);
        Assert.True(reusedPositionProcessing);
        Assert.True(differentConsumerProcessing);
        Assert.Equal(2, inboxEntries.Count);
        Assert.Equal(3, totalInboxEntries);
    }
}
