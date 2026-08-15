using System.Text.Json;
using BuildingBlocks;
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
/// BackorderTimeoutSweeper's own basic behavior, plus the
/// SkuAdvisoryLock guard added alongside it: a restock arriving for a SKU
/// the sweeper is actively timing out must serialize behind (or ahead of)
/// the sweep, never interleave with it - see SkuAdvisoryLock's own comment
/// for the leak/failed-transaction race that guard closes.
/// </summary>
public sealed class BackorderTimeoutSweeperTests : IAsyncLifetime
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
        _serviceProvider = services.BuildServiceProvider();

        await using var scope = _serviceProvider.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<InventoryDbContext>();
        await dbContext.Database.MigrateAsync();
        dbContext.InventoryItems.Add(InventoryItem.Create("SKU-TIMEOUT-001", 0, DateTimeOffset.UtcNow));
        dbContext.WarehouseStocks.Add(WarehouseStock.Create("SKU-TIMEOUT-001", "WH-TEST", 0, reorderPoint: 0, DateTimeOffset.UtcNow));
        await dbContext.SaveChangesAsync();
    }

    public async Task DisposeAsync()
    {
        await _serviceProvider.DisposeAsync();
        await _postgres.DisposeAsync();
    }

    [Fact]
    public async Task ABackorderPastItsWindowIsTimedOutAndRepliedToAsAPermanentRefusal()
    {
        var reservationId = Guid.NewGuid();
        var orderId = Guid.NewGuid();

        await using (var scope = _serviceProvider.CreateAsyncScope())
        {
            var dbContext = scope.ServiceProvider.GetRequiredService<InventoryDbContext>();
            dbContext.Backorders.Add(Backorder.Create(
                reservationId, orderId, "SKU-TIMEOUT-001", 2, "sweep-test-correlation",
                DateTimeOffset.UtcNow.AddMinutes(-200)));
            await dbContext.SaveChangesAsync();
        }

        var sweeper = CreateSweeper(timeoutMinutes: 120);
        await sweeper.SweepAsync(CancellationToken.None);

        await using var assertScope = _serviceProvider.CreateAsyncScope();
        var assertDbContext = assertScope.ServiceProvider.GetRequiredService<InventoryDbContext>();
        Assert.Empty(assertDbContext.Backorders);

        var outboxMessage = await assertDbContext.OutboxMessages.SingleAsync();
        Assert.Equal(nameof(InventoryReservationReplied), outboxMessage.EventType);
        var reply = JsonSerializer.Deserialize<InventoryReservationReplied>(outboxMessage.Payload, SerializerOptions)!;
        Assert.Equal(reservationId, reply.ReservationId);
        Assert.False(reply.Reserved);
        Assert.False(reply.Backordered);
    }

    [Fact]
    public async Task ABackorderStillWithinItsWindowIsLeftAlone()
    {
        var reservationId = Guid.NewGuid();

        await using (var scope = _serviceProvider.CreateAsyncScope())
        {
            var dbContext = scope.ServiceProvider.GetRequiredService<InventoryDbContext>();
            dbContext.Backorders.Add(Backorder.Create(
                reservationId, Guid.NewGuid(), "SKU-TIMEOUT-001", 2, "sweep-test-correlation",
                DateTimeOffset.UtcNow.AddMinutes(-1)));
            await dbContext.SaveChangesAsync();
        }

        var sweeper = CreateSweeper(timeoutMinutes: 120);
        await sweeper.SweepAsync(CancellationToken.None);

        await using var assertScope = _serviceProvider.CreateAsyncScope();
        var assertDbContext = assertScope.ServiceProvider.GetRequiredService<InventoryDbContext>();
        var backorder = await assertDbContext.Backorders.SingleAsync();
        Assert.Equal(reservationId, backorder.ReservationId);
        Assert.Empty(assertDbContext.OutboxMessages);
    }

    private BackorderTimeoutSweeper CreateSweeper(int timeoutMinutes) =>
        new(
            _serviceProvider.GetRequiredService<IServiceScopeFactory>(),
            Options.Create(new BackorderOptions { TimeoutMinutes = timeoutMinutes }),
            NullLogger<BackorderTimeoutSweeper>.Instance);
}
