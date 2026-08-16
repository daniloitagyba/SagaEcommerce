using BuildingBlocks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Orders.Application.Exceptions;
using Orders.Domain;
using Orders.Infrastructure.Data;
using Orders.Infrastructure.Persistence;
using Polly.Registry;

namespace Orders.IntegrationTests;

/// <summary>
/// Closes the gap the audit's finding 6 flagged: before OrderLine's xmin
/// concurrency token (ConfigureOrderLine), two concurrent returns of the
/// same units both read ReturnedQuantity=0, both passed validation against
/// ReturnableQuantity, and both wrote ReturnedQuantity=N - a double refund
/// and a double restock queued, not just a double-counted quantity.
///
/// Reproduced deterministically - both requests read before either writes,
/// then the first save commits and the second is made afterwards - rather
/// than via real thread timing. Optimistic concurrency does not care which
/// wall-clock order the reads happened in, only that both readers' snapshots
/// are stale by the time the second one tries to write, which sequencing the
/// two SaveReturnAsync calls this way guarantees on every run.
/// </summary>
[Collection(PostgresCollectionDefinition.Name)]
public sealed class ConcurrentReturnTests(PostgresFixture fixture) : IAsyncLifetime
{
    private DbContextOptions<OrdersDbContext> _dbOptions = null!;
    private ResiliencePipelineProvider<string> _pipelineProvider = null!;
    private Guid _orderId;

    public async Task InitializeAsync()
    {
        var connectionString = await fixture.CreateSchemaAsync(nameof(ConcurrentReturnTests));
        _dbOptions = new DbContextOptionsBuilder<OrdersDbContext>().UseNpgsql(connectionString).Options;

        await using (var dbContext = new OrdersDbContext(_dbOptions))
        {
            await dbContext.Database.MigrateAsync();

            var order = Order.CreateWithLines(
                "concurrent-return-test-customer",
                "BRL",
                DateTimeOffset.UtcNow,
                couponCode: null,
                [new OrderLineDraft("SKU-CONCURRENT-001", "Test Product", "test-category", 2, 50.00m, 0m, 0m)],
                discountTotal: 0m,
                shippingTotal: 0m,
                taxTotal: 0m,
                paymentMethod: PaymentMethods.Pix,
                shippingAddress: null);

            dbContext.Orders.Add(order);
            await dbContext.SaveChangesAsync();

            // Jumps straight to Delivered - this test is about the return's
            // own concurrency guard, not the lifecycle that gets it there.
            await dbContext.Database.ExecuteSqlInterpolatedAsync(
                $"UPDATE orders SET status = {OrderStatuses.Delivered} WHERE id = {order.Id}");

            _orderId = order.Id;
        }

        _pipelineProvider = new ServiceCollection()
            .AddOrdersResilience()
            .BuildServiceProvider()
            .GetRequiredService<ResiliencePipelineProvider<string>>();
    }

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task TwoConcurrentFullReturnsOfTheSameLineOnlyOneSucceeds()
    {
        // Two separate DbContext instances, matching two separate
        // request-scoped repositories in the real API - a single shared
        // context's own change tracker would hide the race entirely.
        await using var dbContextA = new OrdersDbContext(_dbOptions);
        await using var dbContextB = new OrdersDbContext(_dbOptions);
        var repositoryA = new EfOrderReturnRepository(dbContextA, _pipelineProvider);
        var repositoryB = new EfOrderReturnRepository(dbContextB, _pipelineProvider);

        var orderA = await repositoryA.FindForReturnAsync(_orderId, CancellationToken.None);
        var orderB = await repositoryB.FindForReturnAsync(_orderId, CancellationToken.None);
        Assert.NotNull(orderA);
        Assert.NotNull(orderB);

        var itemsToReturn = new[] { ("SKU-CONCURRENT-001", 2) };

        var (returnA, rejectionA, _) = orderA!.TryReturn(
            itemsToReturn, "customer return", ReturnReasonCategory.Unwanted, TimeSpan.FromDays(30), DateTimeOffset.UtcNow);
        var (returnB, rejectionB, _) = orderB!.TryReturn(
            itemsToReturn, "customer return", ReturnReasonCategory.Unwanted, TimeSpan.FromDays(30), DateTimeOffset.UtcNow);

        // Domain validation alone cannot see the other request in flight -
        // both pass it, exactly as they did before the fix. The guard has
        // to live at the storage layer, which is what this test proves.
        Assert.Equal(ReturnRejectionReason.None, rejectionA);
        Assert.Equal(ReturnRejectionReason.None, rejectionB);

        await repositoryA.SaveReturnAsync(orderA, returnA!, orderA.IsFullyReturned, "concurrent-return-a", CancellationToken.None);

        await Assert.ThrowsAsync<OrderReturnConflictException>(
            () => repositoryB.SaveReturnAsync(orderB, returnB!, orderB.IsFullyReturned, "concurrent-return-b", CancellationToken.None));

        await using var assertDbContext = new OrdersDbContext(_dbOptions);
        var line = await assertDbContext.OrderLines.SingleAsync(l => l.OrderId == _orderId);
        Assert.Equal(2, line.ReturnedQuantity);

        var returns = await assertDbContext.OrderReturns.Where(r => r.OrderId == _orderId).ToListAsync();
        Assert.Single(returns);
    }
}
