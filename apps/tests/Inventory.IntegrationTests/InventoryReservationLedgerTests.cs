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

namespace Inventory.IntegrationTests;

/// <summary>
/// The permanent ledger's two write sides -
/// RecordCommittedAsync (WarehouseAllocationStore) writing on a successful
/// commit, ResolveLedgerOnRestockAsync reducing or removing on a matching
/// restock - proven through the same InventoryReservationMessageProcessor
/// entry points the real reserve/commit/release/restock Kafka topics drive,
/// not called directly, so this exercises the actual wiring in
/// ProcessSettlementAsync, not just the store methods in isolation.
/// </summary>
[Collection(PostgresCollectionDefinition.Name)]
public sealed class InventoryReservationLedgerTests(PostgresFixture fixture) : IAsyncLifetime
{
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);

    private ServiceProvider _serviceProvider = null!;

    public async Task InitializeAsync()
    {
        var connectionString = await fixture.CreateSchemaAsync(nameof(InventoryReservationLedgerTests));

        var services = new ServiceCollection();
        services.AddDbContext<InventoryDbContext>(options => options.UseNpgsql(connectionString));
        services.AddScoped<WarehouseAllocationStore>();
        _serviceProvider = services.BuildServiceProvider();

        await using var scope = _serviceProvider.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<InventoryDbContext>();
        await dbContext.Database.MigrateAsync();
        dbContext.InventoryItems.Add(InventoryItem.Create("SKU-LEDGER-001", 10, DateTimeOffset.UtcNow));
        dbContext.WarehouseStocks.Add(WarehouseStock.Create("SKU-LEDGER-001", "WH-TEST", 10, reorderPoint: 0, DateTimeOffset.UtcNow));
        await dbContext.SaveChangesAsync();
    }

    public async Task DisposeAsync()
    {
        await _serviceProvider.DisposeAsync();
    }

    [Fact]
    public async Task ACommittedReservationWritesALedgerEntry()
    {
        var processor = CreateProcessor();
        var orderId = Guid.NewGuid();
        var reservationId = Guid.NewGuid();

        await processor.ProcessAsync(ReserveConsumeResult(reservationId, orderId, "SKU-LEDGER-001", 4), CancellationToken.None);
        await processor.ProcessCommitAsync(CommitConsumeResult(reservationId, orderId, "SKU-LEDGER-001", 4), CancellationToken.None);

        await using var scope = _serviceProvider.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<InventoryDbContext>();
        var entry = await dbContext.ReservationLedgerEntries.SingleAsync();

        Assert.Equal(orderId, entry.OrderId);
        Assert.Equal("SKU-LEDGER-001", entry.Sku);
        Assert.Equal(4, entry.Quantity);
    }

    [Fact]
    public async Task AReleasedReservationNeverWritesALedgerEntry()
    {
        var processor = CreateProcessor();
        var orderId = Guid.NewGuid();
        var reservationId = Guid.NewGuid();

        await processor.ProcessAsync(ReserveConsumeResult(reservationId, orderId, "SKU-LEDGER-001", 4), CancellationToken.None);
        await processor.ProcessReleaseAsync(ReleaseConsumeResult(reservationId, orderId, "SKU-LEDGER-001", 4), CancellationToken.None);

        await using var scope = _serviceProvider.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<InventoryDbContext>();

        Assert.Empty(dbContext.ReservationLedgerEntries);
    }

    [Fact]
    public async Task AMatchingRestockRemovesTheLedgerEntry()
    {
        var processor = CreateProcessor();
        var orderId = Guid.NewGuid();
        var reservationId = Guid.NewGuid();

        await processor.ProcessAsync(ReserveConsumeResult(reservationId, orderId, "SKU-LEDGER-001", 4), CancellationToken.None);
        await processor.ProcessCommitAsync(CommitConsumeResult(reservationId, orderId, "SKU-LEDGER-001", 4), CancellationToken.None);
        await processor.ProcessRestockAsync(RestockConsumeResult(Guid.NewGuid(), orderId, "SKU-LEDGER-001", 4), CancellationToken.None);

        await using var scope = _serviceProvider.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<InventoryDbContext>();

        Assert.Empty(dbContext.ReservationLedgerEntries);
    }

    [Fact]
    public async Task APartialRestockReducesRatherThanRemovesTheLedgerEntry()
    {
        var processor = CreateProcessor();
        var orderId = Guid.NewGuid();
        var reservationId = Guid.NewGuid();

        await processor.ProcessAsync(ReserveConsumeResult(reservationId, orderId, "SKU-LEDGER-001", 4), CancellationToken.None);
        await processor.ProcessCommitAsync(CommitConsumeResult(reservationId, orderId, "SKU-LEDGER-001", 4), CancellationToken.None);
        await processor.ProcessRestockAsync(RestockConsumeResult(Guid.NewGuid(), orderId, "SKU-LEDGER-001", 1), CancellationToken.None);

        await using var scope = _serviceProvider.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<InventoryDbContext>();
        var entry = await dbContext.ReservationLedgerEntries.SingleAsync();

        Assert.Equal(3, entry.Quantity);
    }

    [Fact]
    public async Task ARestockForAnUnrelatedOrderDoesNotTouchTheLedgerEntry()
    {
        var processor = CreateProcessor();
        var orderId = Guid.NewGuid();
        var reservationId = Guid.NewGuid();

        await processor.ProcessAsync(ReserveConsumeResult(reservationId, orderId, "SKU-LEDGER-001", 4), CancellationToken.None);
        await processor.ProcessCommitAsync(CommitConsumeResult(reservationId, orderId, "SKU-LEDGER-001", 4), CancellationToken.None);
        // The replenishment loop's restocks carry a purchase order's own
        // id, never a real customer order id - this is that case.
        await processor.ProcessRestockAsync(RestockConsumeResult(Guid.NewGuid(), Guid.NewGuid(), "SKU-LEDGER-001", 4), CancellationToken.None);

        await using var scope = _serviceProvider.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<InventoryDbContext>();
        var entry = await dbContext.ReservationLedgerEntries.SingleAsync();

        Assert.Equal(orderId, entry.OrderId);
        Assert.Equal(4, entry.Quantity);
    }

    private InventoryReservationMessageProcessor CreateProcessor()
    {
        var kafkaOptions = Options.Create(new InventoryKafkaOptions());
        return new InventoryReservationMessageProcessor(
            _serviceProvider.GetRequiredService<IServiceScopeFactory>(),
            kafkaOptions,
            NullLogger<InventoryReservationMessageProcessor>.Instance);
    }

    private static ConsumeResult<string, string> ReserveConsumeResult(Guid reservationId, Guid orderId, string sku, int quantity)
    {
        var request = new InventoryReservationRequested(reservationId, orderId, sku, quantity, "ledger-test-correlation", DateTimeOffset.UtcNow);
        return ToConsumeResult("inventory.reservation-requested.v1", sku, request);
    }

    private static ConsumeResult<string, string> CommitConsumeResult(Guid reservationId, Guid orderId, string sku, int quantity)
    {
        var request = new InventoryReservationCommitRequested(reservationId, orderId, sku, quantity, "ledger-test-correlation", DateTimeOffset.UtcNow);
        return ToConsumeResult("inventory.reservation-commit-requested.v1", sku, request);
    }

    private static ConsumeResult<string, string> ReleaseConsumeResult(Guid reservationId, Guid orderId, string sku, int quantity)
    {
        var request = new InventoryReservationReleaseRequested(reservationId, orderId, sku, quantity, "ledger-test-correlation", DateTimeOffset.UtcNow);
        return ToConsumeResult("inventory.reservation-release-requested.v1", sku, request);
    }

    private static ConsumeResult<string, string> RestockConsumeResult(Guid returnId, Guid orderId, string sku, int quantity)
    {
        var request = new InventoryRestockRequested(returnId, orderId, sku, quantity, "ledger-test-correlation", DateTimeOffset.UtcNow);
        return ToConsumeResult("inventory.restock-requested.v1", sku, request);
    }

    private static ConsumeResult<string, string> ToConsumeResult<TRequest>(string topic, string sku, TRequest request)
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
