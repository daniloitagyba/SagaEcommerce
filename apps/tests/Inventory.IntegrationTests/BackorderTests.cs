using BuildingBlocks;
using Confluent.Kafka;
using Inventory.Service;
using Inventory.Service.Data;
using Inventory.Service.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Polly.Registry;
using System.Text.Json;

namespace Inventory.IntegrationTests;

/// <summary>An order the network could not cover right now waits for a restock instead of being cancelled outright.</summary>
[Collection(PostgresCollectionDefinition.Name)]
public sealed class BackorderTests(PostgresFixture fixture) : IAsyncLifetime
{
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);

    private ServiceProvider _serviceProvider = null!;

    public async Task InitializeAsync()
    {
        var connectionString = await fixture.CreateSchemaAsync(nameof(BackorderTests));

        var services = new ServiceCollection();
        services.AddDbContext<InventoryDbContext>(options => options.UseNpgsql(connectionString));
        services.AddScoped<WarehouseAllocationStore>();
        services.AddOrdersResilience();
        _serviceProvider = services.BuildServiceProvider();

        await using var scope = _serviceProvider.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<InventoryDbContext>();
        await dbContext.Database.MigrateAsync();
        dbContext.InventoryItems.Add(InventoryItem.Create("SKU-TEST-001", 3, DateTimeOffset.UtcNow));
        dbContext.WarehouseStocks.Add(WarehouseStock.Create("SKU-TEST-001", "WH-TEST", 3, reorderPoint: 0, DateTimeOffset.UtcNow));
        await dbContext.SaveChangesAsync();
    }

    public async Task DisposeAsync()
    {
        await _serviceProvider.DisposeAsync();
    }

    [Fact]
    public async Task InsufficientStockRecordsABackorderInsteadOfFailingOutright()
    {
        var processor = CreateProcessor();

        var result = await processor.ProcessAsync(CreateReserveConsumeResult(Guid.NewGuid(), "SKU-TEST-001", 5), CancellationToken.None);

        Assert.Equal(MessageProcessingResult.Processed, result);

        await using var scope = _serviceProvider.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<InventoryDbContext>();
        var backorder = await dbContext.Backorders.SingleAsync();
        var reply = await DeserializeReplyAsync(dbContext);

        Assert.Equal("SKU-TEST-001", backorder.Sku);
        Assert.Equal(5, backorder.Quantity);
        Assert.False(reply.Reserved);
        Assert.True(reply.Backordered);
        Assert.Equal("insufficient stock", reply.Reason);

        var item = await dbContext.InventoryItems.SingleAsync(i => i.Sku == "SKU-TEST-001");
        Assert.Equal(3, item.AvailableQuantity);
        Assert.Equal(0, item.ReservedQuantity);
    }

    [Fact]
    public async Task AnUnknownSkuFailsOutrightRatherThanBackordering()
    {
        var processor = CreateProcessor();

        var result = await processor.ProcessAsync(CreateReserveConsumeResult(Guid.NewGuid(), "SKU-DOES-NOT-EXIST", 1), CancellationToken.None);

        Assert.Equal(MessageProcessingResult.Processed, result);

        await using var scope = _serviceProvider.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<InventoryDbContext>();
        var reply = await DeserializeReplyAsync(dbContext);

        Assert.Empty(dbContext.Backorders);
        Assert.False(reply.Reserved);
        Assert.False(reply.Backordered);
        Assert.Equal("unknown sku", reply.Reason);
    }

    [Fact]
    public async Task ARestockReleasesAWaitingBackorderOnTheExactReservationId()
    {
        var processor = CreateProcessor();
        var reservationId = Guid.NewGuid();
        var orderId = Guid.NewGuid();

        var backorderResult = await processor.ProcessAsync(
            CreateReserveConsumeResult(reservationId, "SKU-TEST-001", 5, orderId), CancellationToken.None);
        Assert.Equal(MessageProcessingResult.Processed, backorderResult);

        var restockResult = await processor.ProcessRestockAsync(
            CreateRestockConsumeResult(Guid.NewGuid(), "SKU-TEST-001", 4, orderId), CancellationToken.None);
        Assert.Equal(MessageProcessingResult.Processed, restockResult);

        await using var scope = _serviceProvider.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<InventoryDbContext>();

        Assert.Empty(dbContext.Backorders);

        var replies = await AllRepliesAsync(dbContext);
        var released = Assert.Single(replies, r => r.ReservationId == reservationId && r.Reserved);
        Assert.Null(released.Reason);
        Assert.False(released.Backordered);

        var item = await dbContext.InventoryItems.SingleAsync(i => i.Sku == "SKU-TEST-001");
        Assert.Equal(2, item.AvailableQuantity);
        Assert.Equal(5, item.ReservedQuantity);
    }

    [Fact]
    public async Task CancellingTheWaitingOrderRemovesItsBackorderSoARestockNeverReservesOnItsBehalf()
    {
        var processor = CreateProcessor();
        var reservationId = Guid.NewGuid();
        var orderId = Guid.NewGuid();

        var backorderResult = await processor.ProcessAsync(
            CreateReserveConsumeResult(reservationId, "SKU-TEST-001", 5, orderId), CancellationToken.None);
        Assert.Equal(MessageProcessingResult.Processed, backorderResult);

        var cancelResult = await processor.ProcessBackorderCancellationAsync(
            CreateBackorderCancellationConsumeResult(orderId), CancellationToken.None);
        Assert.Equal(MessageProcessingResult.Processed, cancelResult);

        await using (var scope = _serviceProvider.CreateAsyncScope())
        {
            var dbContext = scope.ServiceProvider.GetRequiredService<InventoryDbContext>();
            Assert.Empty(dbContext.Backorders);
        }

        var restockResult = await processor.ProcessRestockAsync(
            CreateRestockConsumeResult(Guid.NewGuid(), "SKU-TEST-001", 4, orderId), CancellationToken.None);
        Assert.Equal(MessageProcessingResult.Processed, restockResult);

        await using var finalScope = _serviceProvider.CreateAsyncScope();
        var finalDbContext = finalScope.ServiceProvider.GetRequiredService<InventoryDbContext>();
        var item = await finalDbContext.InventoryItems.SingleAsync(i => i.Sku == "SKU-TEST-001");
        Assert.Equal(7, item.AvailableQuantity);
        Assert.Equal(0, item.ReservedQuantity);
    }

    [Fact]
    public async Task CancellingAnOrderWithNoBackorderIsAHarmlessNoOp()
    {
        var processor = CreateProcessor();

        var result = await processor.ProcessBackorderCancellationAsync(
            CreateBackorderCancellationConsumeResult(Guid.NewGuid()), CancellationToken.None);

        Assert.Equal(MessageProcessingResult.Processed, result);
    }

    [Fact]
    public async Task ARestockThatIsNotEnoughLeavesTheBackorderWaiting()
    {
        var processor = CreateProcessor();
        var reservationId = Guid.NewGuid();

        await processor.ProcessAsync(CreateReserveConsumeResult(reservationId, "SKU-TEST-001", 5), CancellationToken.None);

        await processor.ProcessRestockAsync(CreateRestockConsumeResult(Guid.NewGuid(), "SKU-TEST-001", 1), CancellationToken.None);

        await using var scope = _serviceProvider.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<InventoryDbContext>();

        var backorder = await dbContext.Backorders.SingleAsync();
        Assert.Equal(reservationId, backorder.ReservationId);

        var item = await dbContext.InventoryItems.SingleAsync(i => i.Sku == "SKU-TEST-001");
        Assert.Equal(4, item.AvailableQuantity);
        Assert.Equal(0, item.ReservedQuantity);
    }

    /// <summary>Regression coverage for docs/architecture/audit-2026-08-15-domain-and-business-rules-review.md's finding 2: a restock too small for the oldest backorder used to leave every fillable backorder behind it waiting too.</summary>
    [Fact]
    public async Task ARestockSkipsAnUnfillableBackorderInsteadOfBlockingEveryoneBehindIt()
    {
        var processor = CreateProcessor();
        var firstReservationId = Guid.NewGuid();
        var secondReservationId = Guid.NewGuid();

        await processor.ProcessAsync(CreateReserveConsumeResult(Guid.NewGuid(), "SKU-TEST-001", 3), CancellationToken.None);

        await processor.ProcessAsync(CreateReserveConsumeResult(firstReservationId, "SKU-TEST-001", 5), CancellationToken.None);
        await Task.Delay(10);
        await processor.ProcessAsync(CreateReserveConsumeResult(secondReservationId, "SKU-TEST-001", 1), CancellationToken.None);

        await processor.ProcessRestockAsync(CreateRestockConsumeResult(Guid.NewGuid(), "SKU-TEST-001", 1), CancellationToken.None);

        await using var scope = _serviceProvider.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<InventoryDbContext>();

        var stillWaiting = await dbContext.Backorders.Select(backorder => backorder.ReservationId).ToListAsync();
        Assert.Equal([firstReservationId], stillWaiting);

        var replies = await AllRepliesAsync(dbContext);
        Assert.DoesNotContain(replies, r => r.ReservationId == firstReservationId && r.Reserved);
        Assert.Contains(replies, r => r.ReservationId == secondReservationId && r.Reserved);

        var item = await dbContext.InventoryItems.SingleAsync(i => i.Sku == "SKU-TEST-001");
        Assert.Equal(0, item.AvailableQuantity);
        Assert.Equal(4, item.ReservedQuantity);
    }

    private InventoryReservationMessageProcessor CreateProcessor()
    {
        var kafkaOptions = Options.Create(new InventoryKafkaOptions());
        return new InventoryReservationMessageProcessor(
            _serviceProvider.GetRequiredService<IServiceScopeFactory>(),
            kafkaOptions,
            NullLogger<InventoryReservationMessageProcessor>.Instance,
            _serviceProvider.GetRequiredService<ResiliencePipelineProvider<string>>());
    }

    private static async Task<InventoryReservationReplied> DeserializeReplyAsync(InventoryDbContext dbContext)
    {
        var outboxMessage = await dbContext.OutboxMessages.SingleAsync();
        return JsonSerializer.Deserialize<InventoryReservationReplied>(outboxMessage.Payload, SerializerOptions)!;
    }

    private static async Task<List<InventoryReservationReplied>> AllRepliesAsync(InventoryDbContext dbContext)
    {
        var messages = await dbContext.OutboxMessages
            .Where(message => message.EventType == nameof(InventoryReservationReplied))
            .ToListAsync();

        return [.. messages.Select(message => JsonSerializer.Deserialize<InventoryReservationReplied>(message.Payload, SerializerOptions)!)];
    }

    private static ConsumeResult<string, string> CreateReserveConsumeResult(Guid reservationId, string sku, int quantity, Guid? orderId = null)
    {
        var request = new InventoryReservationRequested(
            reservationId, orderId ?? Guid.NewGuid(), sku, quantity, "integration-correlation", DateTimeOffset.UtcNow);

        return new ConsumeResult<string, string>
        {
            Topic = "inventory.reservation-requested.v1",
            Partition = new Partition(0),
            Offset = new Offset(0),
            Message = new Message<string, string> { Key = sku, Value = JsonSerializer.Serialize(request, SerializerOptions), Headers = new Headers() }
        };
    }

    private static ConsumeResult<string, string> CreateRestockConsumeResult(Guid returnId, string sku, int quantity, Guid? orderId = null)
    {
        var request = new InventoryRestockRequested(
            returnId, orderId ?? Guid.NewGuid(), sku, quantity, "integration-correlation", DateTimeOffset.UtcNow);

        return new ConsumeResult<string, string>
        {
            Topic = "inventory.restock-requested.v1",
            Partition = new Partition(0),
            Offset = new Offset(0),
            Message = new Message<string, string> { Key = sku, Value = JsonSerializer.Serialize(request, SerializerOptions), Headers = new Headers() }
        };
    }

    private static ConsumeResult<string, string> CreateBackorderCancellationConsumeResult(Guid orderId)
    {
        var request = new BackorderCancellationRequested(orderId, "integration-correlation", DateTimeOffset.UtcNow);

        return new ConsumeResult<string, string>
        {
            Topic = "inventory.backorder-cancellation-requested.v1",
            Partition = new Partition(0),
            Offset = new Offset(0),
            Message = new Message<string, string> { Key = orderId.ToString("N"), Value = JsonSerializer.Serialize(request, SerializerOptions), Headers = new Headers() }
        };
    }
}
