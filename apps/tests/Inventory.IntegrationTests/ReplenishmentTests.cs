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
using Polly.Registry;

namespace Inventory.IntegrationTests;

/// <summary>End to end at the persistence layer: ReplenishmentRequestProcessor turning a signal into a Requested purchase order, and PurchaseOrderReceivingSweeper turning a due one into a restock command on the outbox.</summary>
[Collection(PostgresCollectionDefinition.Name)]
public sealed class ReplenishmentTests(PostgresFixture fixture) : IAsyncLifetime
{
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);

    private ServiceProvider _serviceProvider = null!;

    public async Task InitializeAsync()
    {
        var connectionString = await fixture.CreateSchemaAsync(nameof(ReplenishmentTests));

        var services = new ServiceCollection();
        services.AddDbContext<InventoryDbContext>(options => options.UseNpgsql(connectionString));
        services.AddOrdersResilience();
        _serviceProvider = services.BuildServiceProvider();

        await using var scope = _serviceProvider.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<InventoryDbContext>();
        await dbContext.Database.MigrateAsync();
    }

    public async Task DisposeAsync()
    {
        await _serviceProvider.DisposeAsync();
    }

    [Fact]
    public async Task AReplenishmentSignalRequestsEnoughToReachTheTargetMultiple()
    {
        var processor = CreateRequestProcessor();
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
        Assert.Equal(purchaseOrderId, request.OrderId);
        Assert.Equal("WH-SP", request.WarehouseCode);
    }

    private ReplenishmentRequestProcessor CreateRequestProcessor() =>
        new(
            _serviceProvider.GetRequiredService<IServiceScopeFactory>(),
            Options.Create(new InventoryKafkaOptions()),
            Options.Create(new ReplenishmentOptions { TargetMultiplier = 3 }),
            NullLogger<ReplenishmentRequestProcessor>.Instance,
            _serviceProvider.GetRequiredService<ResiliencePipelineProvider<string>>());

    private PurchaseOrderReceivingSweeper CreateReceivingSweeper(int leadTimeSeconds) =>
        new(
            _serviceProvider.GetRequiredService<IServiceScopeFactory>(),
            Options.Create(new ReplenishmentOptions { LeadTimeSeconds = leadTimeSeconds }),
            TimeProvider.System,
            NullLogger<PurchaseOrderReceivingSweeper>.Instance,
            _serviceProvider.GetRequiredService<ResiliencePipelineProvider<string>>());

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
