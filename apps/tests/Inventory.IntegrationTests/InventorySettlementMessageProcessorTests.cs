using BuildingBlocks;
using Confluent.Kafka;
using Inventory.Service;
using Inventory.Service.Data;
using Inventory.Service.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using System.Text.Json;
using Testcontainers.PostgreSql;

namespace Inventory.IntegrationTests;

public sealed class InventorySettlementMessageProcessorTests : IAsyncLifetime
{
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);

    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder("postgres:17-alpine")
        .WithDatabase("inventory_test")
        .WithUsername("test_user")
        .WithPassword("test-password-not-a-secret")
        .Build();

    private ServiceProvider _serviceProvider = null!;

    public async Task InitializeAsync()
    {
        await _postgres.StartAsync();

        var services = new ServiceCollection();
        services.AddDbContext<InventoryDbContext>(options => options.UseNpgsql(_postgres.GetConnectionString()));
        services.AddScoped<WarehouseAllocationStore>();
        _serviceProvider = services.BuildServiceProvider();

        await using var scope = _serviceProvider.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<InventoryDbContext>();
        await dbContext.Database.MigrateAsync();
        var item = InventoryItem.Create("SKU-TEST-001", 10, DateTimeOffset.UtcNow);
        item.TryReserve(4, DateTimeOffset.UtcNow);
        dbContext.InventoryItems.Add(item);
        // Needed since Milestone 72 refuses reservation outright with no
        // warehouse network - the 6 here mirrors the item's own Available
        // after the 4 already reserved, keeping aggregate and network in agreement.
        dbContext.WarehouseStocks.Add(WarehouseStock.Create("SKU-TEST-001", "WH-TEST", 6, reorderPoint: 0, DateTimeOffset.UtcNow));
        await dbContext.SaveChangesAsync();
    }

    public async Task DisposeAsync()
    {
        await _serviceProvider.DisposeAsync();
        await _postgres.DisposeAsync();
    }

    [Fact]
    public async Task ProcessCommitAsyncTurnsTheHoldIntoAPermanentDeduction()
    {
        var processor = CreateProcessor();
        var reservationId = Guid.NewGuid();

        var result = await processor.ProcessCommitAsync(CreateCommitConsumeResult(reservationId, "SKU-TEST-001", 4), CancellationToken.None);

        Assert.Equal(MessageProcessingResult.Processed, result);

        await using var scope = _serviceProvider.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<InventoryDbContext>();
        var item = await dbContext.InventoryItems.SingleAsync(i => i.Sku == "SKU-TEST-001");
        var outboxMessage = await dbContext.OutboxMessages.SingleAsync();
        var reply = JsonSerializer.Deserialize<InventoryReservationCommitReplied>(outboxMessage.Payload, SerializerOptions)!;

        // Available stays at 6 (never returns) - Reserved drops from 4 to 0.
        Assert.Equal(6, item.AvailableQuantity);
        Assert.Equal(0, item.ReservedQuantity);
        Assert.True(reply.Committed);
    }

    [Fact]
    public async Task ProcessReleaseAsyncGivesTheHeldStockBackToAvailable()
    {
        var processor = CreateProcessor();
        var reservationId = Guid.NewGuid();

        var result = await processor.ProcessReleaseAsync(CreateReleaseConsumeResult(reservationId, "SKU-TEST-001", 4), CancellationToken.None);

        Assert.Equal(MessageProcessingResult.Processed, result);

        await using var scope = _serviceProvider.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<InventoryDbContext>();
        var item = await dbContext.InventoryItems.SingleAsync(i => i.Sku == "SKU-TEST-001");
        var outboxMessage = await dbContext.OutboxMessages.SingleAsync();
        var reply = JsonSerializer.Deserialize<InventoryReservationReleaseReplied>(outboxMessage.Payload, SerializerOptions)!;

        // Available returns to the original 10 - Reserved drops back to 0.
        Assert.Equal(10, item.AvailableQuantity);
        Assert.Equal(0, item.ReservedQuantity);
        Assert.True(reply.Released);
    }

    [Fact]
    public async Task CommitAndReleaseReuseTheSameReservationIdWithoutInboxCollision()
    {
        // Commit and Release use a different inbox consumer_name suffix than Reserve, so reusing the ReservationId isn't mistaken for a duplicate.
        var processor = CreateProcessor();
        var reservationId = Guid.NewGuid();

        var reserveResult = await processor.ProcessAsync(CreateReserveConsumeResult(reservationId, "SKU-TEST-001", 2), CancellationToken.None);
        var commitResult = await processor.ProcessCommitAsync(CreateCommitConsumeResult(reservationId, "SKU-TEST-001", 2), CancellationToken.None);

        Assert.Equal(MessageProcessingResult.Processed, reserveResult);
        Assert.Equal(MessageProcessingResult.Processed, commitResult);

        await using var scope = _serviceProvider.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<InventoryDbContext>();
        var item = await dbContext.InventoryItems.SingleAsync(i => i.Sku == "SKU-TEST-001");

        // Started at Available=6/Reserved=4 (from InitializeAsync); Reserve
        // 2 more (Available=4/Reserved=6), then Commit those same 2
        // (Available stays 4, Reserved drops to 4).
        Assert.Equal(4, item.AvailableQuantity);
        Assert.Equal(4, item.ReservedQuantity);
    }

    [Fact]
    public async Task ProcessCommitAsyncSkipsADuplicateReservationId()
    {
        var processor = CreateProcessor();
        var reservationId = Guid.NewGuid();

        var first = await processor.ProcessCommitAsync(CreateCommitConsumeResult(reservationId, "SKU-TEST-001", 4), CancellationToken.None);
        var second = await processor.ProcessCommitAsync(CreateCommitConsumeResult(reservationId, "SKU-TEST-001", 4), CancellationToken.None);

        Assert.Equal(MessageProcessingResult.Processed, first);
        Assert.Equal(MessageProcessingResult.Duplicate, second);

        await using var scope = _serviceProvider.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<InventoryDbContext>();
        var item = await dbContext.InventoryItems.SingleAsync(i => i.Sku == "SKU-TEST-001");

        Assert.Equal(0, item.ReservedQuantity);
        Assert.Equal(1, await dbContext.OutboxMessages.CountAsync());
    }

    private InventoryReservationMessageProcessor CreateProcessor()
    {
        var kafkaOptions = Options.Create(new InventoryKafkaOptions());
        return new InventoryReservationMessageProcessor(
            _serviceProvider.GetRequiredService<IServiceScopeFactory>(),
            kafkaOptions,
            NullLogger<InventoryReservationMessageProcessor>.Instance);
    }

    private static ConsumeResult<string, string> CreateReserveConsumeResult(Guid reservationId, string sku, int quantity)
    {
        var request = new InventoryReservationRequested(reservationId, Guid.NewGuid(), sku, quantity, "integration-correlation", DateTimeOffset.UtcNow);
        return CreateConsumeResult("inventory.reservation-requested.v1", sku, request);
    }

    private static ConsumeResult<string, string> CreateCommitConsumeResult(Guid reservationId, string sku, int quantity)
    {
        var request = new InventoryReservationCommitRequested(reservationId, Guid.NewGuid(), sku, quantity, "integration-correlation", DateTimeOffset.UtcNow);
        return CreateConsumeResult("inventory.reservation-commit-requested.v1", sku, request);
    }

    private static ConsumeResult<string, string> CreateReleaseConsumeResult(Guid reservationId, string sku, int quantity)
    {
        var request = new InventoryReservationReleaseRequested(reservationId, Guid.NewGuid(), sku, quantity, "integration-correlation", DateTimeOffset.UtcNow);
        return CreateConsumeResult("inventory.reservation-release-requested.v1", sku, request);
    }

    private static ConsumeResult<string, string> CreateConsumeResult<TRequest>(string topic, string sku, TRequest request)
    {
        return new ConsumeResult<string, string>
        {
            Topic = topic,
            Partition = new Partition(0),
            Offset = new Offset(0),
            Message = new Message<string, string>
            {
                Key = sku,
                Value = JsonSerializer.Serialize(request, SerializerOptions),
                Headers = new Headers()
            }
        };
    }
}
