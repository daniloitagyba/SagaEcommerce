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

/// <summary>
/// Milestone 74: an order the network could not cover right now waits for
/// a restock instead of being cancelled outright.
/// </summary>
public sealed class BackorderTests : IAsyncLifetime
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
        dbContext.InventoryItems.Add(InventoryItem.Create("SKU-TEST-001", 3, DateTimeOffset.UtcNow));
        dbContext.WarehouseStocks.Add(WarehouseStock.Create("SKU-TEST-001", "WH-TEST", 3, reorderPoint: 0, DateTimeOffset.UtcNow));
        await dbContext.SaveChangesAsync();
    }

    public async Task DisposeAsync()
    {
        await _serviceProvider.DisposeAsync();
        await _postgres.DisposeAsync();
    }

    [Fact]
    public async Task InsufficientStockRecordsABackorderInsteadOfFailingOutright()
    {
        var processor = CreateProcessor();

        // Only 3 available; asking for 5 cannot be covered by anything on
        // the shelf right now, but nothing says it never will be.
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

        // Nothing was drawn down - a backorder is a promise to try again,
        // not a partial reservation.
        var item = await dbContext.InventoryItems.SingleAsync(i => i.Sku == "SKU-TEST-001");
        Assert.Equal(3, item.AvailableQuantity);
        Assert.Equal(0, item.ReservedQuantity);
    }

    [Fact]
    public async Task AnUnknownSkuFailsOutrightRatherThanBackordering()
    {
        // Nothing will ever restock a SKU that does not exist - recording a
        // backorder for it would wait forever for a restock that can never
        // come.
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
        // Two replies share this reservationId on purpose - the first
        // "wait" and the eventual "yes" - so the release is identified by
        // Reserved: true, not by the id alone.
        var released = Assert.Single(replies, r => r.ReservationId == reservationId && r.Reserved);
        Assert.Null(released.Reason);
        Assert.False(released.Backordered);

        // 3 original + 4 restocked - 5 released to the backorder = 2 left.
        var item = await dbContext.InventoryItems.SingleAsync(i => i.Sku == "SKU-TEST-001");
        Assert.Equal(2, item.AvailableQuantity);
        Assert.Equal(5, item.ReservedQuantity);
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

    [Fact]
    public async Task RestockReleasesBackordersOldestFirstAndStopsAtTheFirstThatStillDoesNotFit()
    {
        var processor = CreateProcessor();
        var firstReservationId = Guid.NewGuid();
        var secondReservationId = Guid.NewGuid();

        // Deplete the shelf completely first, so both requests below are
        // genuinely backordered rather than one of them succeeding outright
        // against whatever was left.
        await processor.ProcessAsync(CreateReserveConsumeResult(Guid.NewGuid(), "SKU-TEST-001", 3), CancellationToken.None);

        // First in line asks for 5, which the restock below still will not
        // cover. Second asks for only 1 - trivially fulfillable in
        // isolation once that restock lands - but arrived later, so
        // serving it while the first still waits would be skipping the
        // line just because it is smaller.
        await processor.ProcessAsync(CreateReserveConsumeResult(firstReservationId, "SKU-TEST-001", 5), CancellationToken.None);
        await Task.Delay(10); // RequestedAt must strictly order the two.
        await processor.ProcessAsync(CreateReserveConsumeResult(secondReservationId, "SKU-TEST-001", 1), CancellationToken.None);

        // Only 1 comes back - covers the second backorder alone, nowhere
        // near the first's 5. The loop must stop at the first rather than
        // notice the second would fit.
        await processor.ProcessRestockAsync(CreateRestockConsumeResult(Guid.NewGuid(), "SKU-TEST-001", 1), CancellationToken.None);

        await using var scope = _serviceProvider.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<InventoryDbContext>();

        var stillWaiting = await dbContext.Backorders.Select(backorder => backorder.ReservationId).ToListAsync();
        Assert.Equal(
            new[] { firstReservationId, secondReservationId }.OrderBy(id => id),
            stillWaiting.OrderBy(id => id));

        var replies = await AllRepliesAsync(dbContext);
        // Only the two backorders matter here - the earlier "deplete the
        // shelf" reservation legitimately succeeded and shows up too.
        Assert.DoesNotContain(replies, r => r.ReservationId == firstReservationId && r.Reserved);
        Assert.DoesNotContain(replies, r => r.ReservationId == secondReservationId && r.Reserved);

        // Untouched: neither backorder was released, so the restocked unit
        // is still sitting in Available. Reserved is 3 from the initial
        // "deplete the shelf" reservation above, not from either backorder.
        var item = await dbContext.InventoryItems.SingleAsync(i => i.Sku == "SKU-TEST-001");
        Assert.Equal(1, item.AvailableQuantity);
        Assert.Equal(3, item.ReservedQuantity);
    }

    private InventoryReservationMessageProcessor CreateProcessor()
    {
        var kafkaOptions = Options.Create(new InventoryKafkaOptions());
        return new InventoryReservationMessageProcessor(
            _serviceProvider.GetRequiredService<IServiceScopeFactory>(),
            kafkaOptions,
            NullLogger<InventoryReservationMessageProcessor>.Instance);
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
}
