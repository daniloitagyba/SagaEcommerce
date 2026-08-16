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

/// <summary>
/// An order the network could not cover right now waits for
/// a restock instead of being cancelled outright.
/// </summary>
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

        // Only 3 available; asking for 5 can't be covered now, but nothing says it never will be.
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

        // Nothing was drawn down - a backorder is a promise to try again, not a partial reservation.
        var item = await dbContext.InventoryItems.SingleAsync(i => i.Sku == "SKU-TEST-001");
        Assert.Equal(3, item.AvailableQuantity);
        Assert.Equal(0, item.ReservedQuantity);
    }

    [Fact]
    public async Task AnUnknownSkuFailsOutrightRatherThanBackordering()
    {
        // Nothing will ever restock a SKU that doesn't exist.
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

        // Enough comes back to cover the waiting order.
        var restockResult = await processor.ProcessRestockAsync(
            CreateRestockConsumeResult(Guid.NewGuid(), "SKU-TEST-001", 4, orderId), CancellationToken.None);
        Assert.Equal(MessageProcessingResult.Processed, restockResult);

        await using var scope = _serviceProvider.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<InventoryDbContext>();

        Assert.Empty(dbContext.Backorders);

        var replies = await AllRepliesAsync(dbContext);
        // Two replies share this reservationId (the "wait" and the eventual "yes"), so release is identified by Reserved: true, not the id alone.
        var released = Assert.Single(replies, r => r.ReservationId == reservationId && r.Reserved);
        Assert.Null(released.Reason);
        Assert.False(released.Backordered);

        // 3 original + 4 restocked - 5 released to the backorder = 2 left.
        var item = await dbContext.InventoryItems.SingleAsync(i => i.Sku == "SKU-TEST-001");
        Assert.Equal(2, item.AvailableQuantity);
        Assert.Equal(5, item.ReservedQuantity);
    }

    [Fact]
    public async Task CancellingTheWaitingOrderRemovesItsBackorderSoARestockNeverReservesOnItsBehalf()
    {
        // The order this backorder belonged to was cancelled
        // while still waiting on stock. Nothing used to tell Inventory that -
        // the row sat there until a restock came in
        // and blindly reserved for a saga that no longer existed, ahead of
        // whoever was actually still waiting (strict FIFO means it would
        // have been served first).
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

        // A later restock must not reserve on behalf of the cancelled order - there is no waiting backorder left to release.
        var restockResult = await processor.ProcessRestockAsync(
            CreateRestockConsumeResult(Guid.NewGuid(), "SKU-TEST-001", 4, orderId), CancellationToken.None);
        Assert.Equal(MessageProcessingResult.Processed, restockResult);

        await using var finalScope = _serviceProvider.CreateAsyncScope();
        var finalDbContext = finalScope.ServiceProvider.GetRequiredService<InventoryDbContext>();
        var item = await finalDbContext.InventoryItems.SingleAsync(i => i.Sku == "SKU-TEST-001");
        // 3 original + 4 restocked, nothing reserved on the cancelled order's behalf.
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

        // 3 + 1 = 4, still short of the 5 this backorder needs.
        await processor.ProcessRestockAsync(CreateRestockConsumeResult(Guid.NewGuid(), "SKU-TEST-001", 1), CancellationToken.None);

        await using var scope = _serviceProvider.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<InventoryDbContext>();

        var backorder = await dbContext.Backorders.SingleAsync();
        Assert.Equal(reservationId, backorder.ReservationId);

        var item = await dbContext.InventoryItems.SingleAsync(i => i.Sku == "SKU-TEST-001");
        Assert.Equal(4, item.AvailableQuantity);
        Assert.Equal(0, item.ReservedQuantity);
    }

    /// <summary>
    /// Regression coverage for
    /// docs/architecture/audit-2026-08-15-domain-and-business-rules-review.md's
    /// finding 2: a restock too small for the oldest backorder used to
    /// leave every fillable backorder behind it waiting too (the loop
    /// stopped at the first miss instead of trying the rest), and
    /// BackorderTimeoutSweeper would eventually cancel all of them,
    /// including ones the restock could actually have covered. Renamed
    /// from RestockReleasesBackordersOldestFirstAndStopsAtTheFirstThatStillDoesNotFit,
    /// which pinned exactly that behavior before the fix.
    /// </summary>
    [Fact]
    public async Task ARestockSkipsAnUnfillableBackorderInsteadOfBlockingEveryoneBehindIt()
    {
        var processor = CreateProcessor();
        var firstReservationId = Guid.NewGuid();
        var secondReservationId = Guid.NewGuid();

        // Deplete the shelf first, so both requests below are genuinely backordered, not succeeding against leftover stock.
        await processor.ProcessAsync(CreateReserveConsumeResult(Guid.NewGuid(), "SKU-TEST-001", 3), CancellationToken.None);

        // First asks for 5, which the restock below still won't cover.
        // Second asks for only 1 and arrived later - it can be filled from
        // this exact restock even though it isn't first in line.
        await processor.ProcessAsync(CreateReserveConsumeResult(firstReservationId, "SKU-TEST-001", 5), CancellationToken.None);
        await Task.Delay(10); // RequestedAt must strictly order the two.
        await processor.ProcessAsync(CreateReserveConsumeResult(secondReservationId, "SKU-TEST-001", 1), CancellationToken.None);

        // Only 1 comes back - nowhere near the first's 5, but exactly enough for the second. The loop must not give up on the second because the first missed.
        await processor.ProcessRestockAsync(CreateRestockConsumeResult(Guid.NewGuid(), "SKU-TEST-001", 1), CancellationToken.None);

        await using var scope = _serviceProvider.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<InventoryDbContext>();

        // Only the larger, still-unfillable backorder remains.
        var stillWaiting = await dbContext.Backorders.Select(backorder => backorder.ReservationId).ToListAsync();
        Assert.Equal([firstReservationId], stillWaiting);

        var replies = await AllRepliesAsync(dbContext);
        Assert.DoesNotContain(replies, r => r.ReservationId == firstReservationId && r.Reserved);
        Assert.Contains(replies, r => r.ReservationId == secondReservationId && r.Reserved);

        // The restocked unit went to the second backorder, not left sitting in Available. Reserved: 3 (initial "deplete") + 1 (second backorder) = 4.
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
