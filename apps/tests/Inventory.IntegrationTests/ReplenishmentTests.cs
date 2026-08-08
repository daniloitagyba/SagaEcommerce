using System.Text.Json;
using BuildingBlocks;
using Confluent.Kafka;
using Inventory.Service;
using Inventory.Service.Data;
using Inventory.Service.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Testcontainers.PostgreSql;

namespace Inventory.IntegrationTests;

/// <summary>
/// Milestone 89: end to end at the persistence layer -
/// ReplenishmentRequestProcessor turning a WarehouseReplenishmentNeeded
/// signal into a Requested purchase order, and
/// PurchaseOrderReceivingSweeper turning a due one into an actual restock
/// command on the outbox. What happens after the outbox row is dispatched
/// (the produce to inventory.restock-requested.v1, and
/// InventoryReservationMessageProcessor.ProcessRestockAsync picking it back
/// up) is exactly Milestone 74's already-tested restock/backorder-release
/// path - not re-tested here.
/// </summary>
public sealed class ReplenishmentTests : IAsyncLifetime
{
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);

    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder("postgres:17-alpine")
        .WithDatabase("inventory_replenishment_test")
        .WithUsername("test_user")
        .WithPassword("test-password-not-a-secret")
        .Build();

    private ServiceProvider _serviceProvider = null!;

    public async Task InitializeAsync()
    {
        await _postgres.StartAsync();

        var services = new ServiceCollection();
        services.AddDbContext<InventoryDbContext>(options => options.UseNpgsql(_postgres.GetConnectionString()));
        _serviceProvider = services.BuildServiceProvider();

        await using var scope = _serviceProvider.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<InventoryDbContext>();
        await dbContext.Database.MigrateAsync();
    }

    public async Task DisposeAsync()
    {
        await _serviceProvider.DisposeAsync();
        await _postgres.DisposeAsync();
    }

    [Fact]
    public async Task AReplenishmentSignalRequestsEnoughToReachTheTargetMultiple()
    {
        var processor = CreateRequestProcessor();
        // 4 available, reorder point 5, multiplier 3 -> target 15, request 15 - 4 = 11.
        var result = await processor.ProcessAsync(
            CreateSignalConsumeResult(Guid.NewGuid(), "SKU-A", "WH-SP", available: 4, reorderPoint: 5), CancellationToken.None);

        Assert.Equal(MessageProcessingResult.Processed, result);

        await using var scope = _serviceProvider.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<InventoryDbContext>();
        var purchaseOrder = await dbContext.PurchaseOrders.SingleAsync();

        Assert.Equal("SKU-A", purchaseOrder.Sku);
        Assert.Equal("WH-SP", purchaseOrder.WarehouseCode);
        Assert.Equal(11, purchaseOrder.Quantity);
        Assert.Equal(PurchaseOrderStates.Requested, purchaseOrder.State);
    }

    [Fact]
    public async Task ARedeliveredSignalDoesNotCreateASecondPurchaseOrder()
    {
        var processor = CreateRequestProcessor();
        var eventId = Guid.NewGuid();

        var first = await processor.ProcessAsync(
            CreateSignalConsumeResult(eventId, "SKU-B", "WH-SP", available: 2, reorderPoint: 5), CancellationToken.None);
        var second = await processor.ProcessAsync(
            CreateSignalConsumeResult(eventId, "SKU-B", "WH-SP", available: 2, reorderPoint: 5), CancellationToken.None);

        Assert.Equal(MessageProcessingResult.Processed, first);
        Assert.Equal(MessageProcessingResult.Duplicate, second);

        await using var scope = _serviceProvider.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<InventoryDbContext>();
        Assert.Single(dbContext.PurchaseOrders);
    }

    [Fact]
    public async Task TheReceivingSweepLeavesAPurchaseOrderAloneBeforeItsLeadTimeElapses()
    {
        await using (var scope = _serviceProvider.CreateAsyncScope())
        {
            var dbContext = scope.ServiceProvider.GetRequiredService<InventoryDbContext>();
            dbContext.PurchaseOrders.Add(PurchaseOrder.Create("SKU-C", "WH-SP", 10, "correlation-1", DateTimeOffset.UtcNow));
            await dbContext.SaveChangesAsync();
        }

        var sweeper = CreateReceivingSweeper(leadTimeSeconds: 3600);
        await sweeper.SweepAsync(CancellationToken.None);

        await using var verifyScope = _serviceProvider.CreateAsyncScope();
        var verifyDbContext = verifyScope.ServiceProvider.GetRequiredService<InventoryDbContext>();
        var purchaseOrder = await verifyDbContext.PurchaseOrders.SingleAsync();
        Assert.Equal(PurchaseOrderStates.Requested, purchaseOrder.State);
        Assert.Empty(verifyDbContext.OutboxMessages);
    }

    [Fact]
    public async Task TheReceivingSweepMarksADuePurchaseOrderReceivedAndQueuesARestock()
    {
        Guid purchaseOrderId;
        await using (var scope = _serviceProvider.CreateAsyncScope())
        {
            var dbContext = scope.ServiceProvider.GetRequiredService<InventoryDbContext>();
            var purchaseOrder = PurchaseOrder.Create("SKU-D", "WH-SP", 12, "correlation-1", DateTimeOffset.UtcNow.AddMinutes(-5));
            purchaseOrderId = purchaseOrder.Id;
            dbContext.PurchaseOrders.Add(purchaseOrder);
            await dbContext.SaveChangesAsync();
        }

        // Lead time already elapsed (5 minutes ago vs. a 1-second window).
        var sweeper = CreateReceivingSweeper(leadTimeSeconds: 1);
        await sweeper.SweepAsync(CancellationToken.None);

        await using var verifyScope = _serviceProvider.CreateAsyncScope();
        var verifyDbContext = verifyScope.ServiceProvider.GetRequiredService<InventoryDbContext>();
        var purchaseOrderAfter = await verifyDbContext.PurchaseOrders.SingleAsync(p => p.Id == purchaseOrderId);
        Assert.Equal(PurchaseOrderStates.Received, purchaseOrderAfter.State);
        Assert.NotNull(purchaseOrderAfter.ReceivedAt);

        var outboxMessage = await verifyDbContext.OutboxMessages.SingleAsync();
        Assert.Equal(nameof(InventoryRestockRequested), outboxMessage.EventType);
        var request = JsonSerializer.Deserialize<InventoryRestockRequested>(outboxMessage.Payload, SerializerOptions)!;
        Assert.Equal("SKU-D", request.Sku);
        Assert.Equal(12, request.Quantity);
        // The purchase order's own id stands in for OrderId - there is no real customer order behind a replenishment restock.
        Assert.Equal(purchaseOrderId, request.OrderId);
    }

    private ReplenishmentRequestProcessor CreateRequestProcessor() =>
        new(
            _serviceProvider.GetRequiredService<IServiceScopeFactory>(),
            Options.Create(new InventoryKafkaOptions()),
            Options.Create(new ReplenishmentOptions { TargetMultiplier = 3 }),
            NullLogger<ReplenishmentRequestProcessor>.Instance);

    private PurchaseOrderReceivingSweeper CreateReceivingSweeper(int leadTimeSeconds) =>
        new(
            _serviceProvider.GetRequiredService<IServiceScopeFactory>(),
            Options.Create(new ReplenishmentOptions { LeadTimeSeconds = leadTimeSeconds }),
            NullLogger<PurchaseOrderReceivingSweeper>.Instance);

    private static ConsumeResult<string, string> CreateSignalConsumeResult(
        Guid eventId, string sku, string warehouseCode, int available, int reorderPoint)
    {
        var signal = new WarehouseReplenishmentNeeded(eventId, sku, warehouseCode, available, reorderPoint, "integration-correlation", DateTimeOffset.UtcNow);
        return new ConsumeResult<string, string>
        {
            Topic = "inventory.replenishment-needed.v1",
            Partition = new Partition(0),
            Offset = new Offset(0),
            Message = new Message<string, string> { Key = sku, Value = JsonSerializer.Serialize(signal, SerializerOptions), Headers = new Headers() }
        };
    }
}
