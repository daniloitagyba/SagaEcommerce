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

public sealed class InventoryReservationMessageProcessorTests : IAsyncLifetime
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
        dbContext.InventoryItems.Add(InventoryItem.Create("SKU-TEST-001", 10, DateTimeOffset.UtcNow));
        // Milestone 72 made the allocator (not just the aggregate row) the
        // thing that decides whether a reservation is fulfillable - a SKU
        // with no warehouse_stock rows at all is an empty candidate list,
        // which StockAllocator.Allocate always refuses. The aggregate row
        // above and the network below have to agree on how much exists,
        // same invariant the M72 seed migration keeps on the real database.
        dbContext.WarehouseStocks.Add(WarehouseStock.Create("SKU-TEST-001", "WH-TEST", 10, reorderPoint: 0, DateTimeOffset.UtcNow));
        await dbContext.SaveChangesAsync();
    }

    public async Task DisposeAsync()
    {
        await _serviceProvider.DisposeAsync();
        await _postgres.DisposeAsync();
    }

    [Fact]
    public async Task ProcessAsyncReservesWhenStockIsAvailable()
    {
        var processor = CreateProcessor();

        var result = await processor.ProcessAsync(CreateConsumeResult(Guid.NewGuid(), "SKU-TEST-001", 4), CancellationToken.None);

        Assert.Equal(MessageProcessingResult.Processed, result);

        await using var scope = _serviceProvider.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<InventoryDbContext>();
        var item = await dbContext.InventoryItems.SingleAsync(i => i.Sku == "SKU-TEST-001");
        var outboxMessage = await dbContext.OutboxMessages.SingleAsync();
        var reply = JsonSerializer.Deserialize<InventoryReservationReplied>(outboxMessage.Payload, SerializerOptions)!;

        Assert.Equal(6, item.AvailableQuantity);
        Assert.Equal(4, item.ReservedQuantity);
        Assert.True(reply.Reserved);
        Assert.Null(reply.Reason);
    }

    [Fact]
    public async Task ProcessAsyncDeclinesWhenStockIsInsufficient()
    {
        var processor = CreateProcessor();

        var result = await processor.ProcessAsync(CreateConsumeResult(Guid.NewGuid(), "SKU-TEST-001", 11), CancellationToken.None);

        Assert.Equal(MessageProcessingResult.Processed, result);

        await using var scope = _serviceProvider.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<InventoryDbContext>();
        var item = await dbContext.InventoryItems.SingleAsync(i => i.Sku == "SKU-TEST-001");
        var outboxMessage = await dbContext.OutboxMessages.SingleAsync();
        var reply = JsonSerializer.Deserialize<InventoryReservationReplied>(outboxMessage.Payload, SerializerOptions)!;

        Assert.Equal(10, item.AvailableQuantity);
        Assert.False(reply.Reserved);
        Assert.Equal("insufficient stock", reply.Reason);
    }

    [Fact]
    public async Task TwoSequentialReservationsCumulativelyDepleteTheSameSku()
    {
        var processor = CreateProcessor();

        var first = await processor.ProcessAsync(CreateConsumeResult(Guid.NewGuid(), "SKU-TEST-001", 6), CancellationToken.None);
        var second = await processor.ProcessAsync(CreateConsumeResult(Guid.NewGuid(), "SKU-TEST-001", 5), CancellationToken.None);

        Assert.Equal(MessageProcessingResult.Processed, first);
        Assert.Equal(MessageProcessingResult.Processed, second);

        await using var scope = _serviceProvider.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<InventoryDbContext>();
        var item = await dbContext.InventoryItems.SingleAsync(i => i.Sku == "SKU-TEST-001");
        var outboxMessages = await dbContext.OutboxMessages.ToListAsync();
        var replies = outboxMessages
            .Select(m => JsonSerializer.Deserialize<InventoryReservationReplied>(m.Payload, SerializerOptions)!)
            .ToList();

        Assert.Equal(4, item.AvailableQuantity);
        Assert.Equal(6, item.ReservedQuantity);
        Assert.Single(replies, r => r.Reserved);
        Assert.Single(replies, r => !r.Reserved);
    }

    [Fact]
    public async Task ProcessAsyncSkipsDuplicateReservationIds()
    {
        var processor = CreateProcessor();
        var reservationId = Guid.NewGuid();

        var firstResult = await processor.ProcessAsync(CreateConsumeResult(reservationId, "SKU-TEST-001", 4), CancellationToken.None);
        var secondResult = await processor.ProcessAsync(CreateConsumeResult(reservationId, "SKU-TEST-001", 4), CancellationToken.None);

        Assert.Equal(MessageProcessingResult.Processed, firstResult);
        Assert.Equal(MessageProcessingResult.Duplicate, secondResult);

        await using var scope = _serviceProvider.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<InventoryDbContext>();
        var item = await dbContext.InventoryItems.SingleAsync(i => i.Sku == "SKU-TEST-001");

        Assert.Equal(6, item.AvailableQuantity);
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

    private static ConsumeResult<string, string> CreateConsumeResult(Guid reservationId, string sku, int quantity)
    {
        var request = new InventoryReservationRequested(
            reservationId,
            Guid.NewGuid(),
            sku,
            quantity,
            "integration-correlation",
            DateTimeOffset.UtcNow);

        return new ConsumeResult<string, string>
        {
            Topic = "inventory.reservation-requested.v1",
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
