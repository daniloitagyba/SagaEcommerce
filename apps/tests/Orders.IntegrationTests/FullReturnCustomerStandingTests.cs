using BuildingBlocks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Orders.Domain;
using Orders.Infrastructure.Data;
using Orders.Infrastructure.Persistence;
using Polly.Registry;
using Testcontainers.PostgreSql;

namespace Orders.IntegrationTests;

/// <summary>
/// Regression coverage for
/// docs/architecture/audit-2026-08-15-domain-and-business-rules-review.md's
/// finding 1: a full return used to leave the customer permanently
/// credited with the standing a since-refunded order earned them, because
/// Customer.ReverseCompletedOrder had no production caller at all.
/// EfOrderReturnRepository.SaveReturnAsync now reverses it when
/// markOrderReturned is true - gated on the same Delivered -> Returned
/// race guard that also queues the OrderStatusChanged event, so a losing
/// concurrent request cannot reverse it twice.
/// </summary>
public sealed class FullReturnCustomerStandingTests : IAsyncLifetime
{
    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder("postgres:17-alpine")
        .WithDatabase("orders_test")
        .WithUsername("test_user")
        .WithPassword("test-password-not-a-secret")
        .Build();

    private DbContextOptions<OrdersDbContext> _dbOptions = null!;
    private ResiliencePipelineProvider<string> _pipelineProvider = null!;

    public async Task InitializeAsync()
    {
        await _postgres.StartAsync();
        _dbOptions = new DbContextOptionsBuilder<OrdersDbContext>().UseNpgsql(_postgres.GetConnectionString()).Options;

        await using var dbContext = new OrdersDbContext(_dbOptions);
        await dbContext.Database.MigrateAsync();

        _pipelineProvider = new ServiceCollection()
            .AddOrdersResilience()
            .BuildServiceProvider()
            .GetRequiredService<ResiliencePipelineProvider<string>>();
    }

    public async Task DisposeAsync() => await _postgres.DisposeAsync();

    [Fact]
    public async Task AFullReturnReversesCustomerStanding()
    {
        const string customerId = "full-return-standing-customer";
        Guid orderId;

        await using (var dbContext = new OrdersDbContext(_dbOptions))
        {
            // RecordCompletedOrder is exactly what OrderStatusStore/
            // EfOrderStatusRepository call when this order was originally
            // confirmed - seeding through the same domain method the
            // production path uses, not a raw UPDATE, so this test
            // exercises the real round trip: recorded on confirmation,
            // reversed on full return.
            var customer = Customer.Create(customerId, DateTimeOffset.UtcNow.AddDays(-30));
            customer.RecordCompletedOrder(300m);
            dbContext.Customers.Add(customer);

            var order = Order.CreateWithLines(
                customerId,
                "BRL",
                DateTimeOffset.UtcNow,
                couponCode: null,
                [new OrderLineDraft("SKU-FULL-RETURN-001", "Test Product", "test-category", 1, 300m, 0m, 0m)],
                discountTotal: 0m,
                shippingTotal: 0m,
                taxTotal: 0m,
                paymentMethod: PaymentMethods.Pix,
                shippingAddress: null);
            dbContext.Orders.Add(order);
            await dbContext.SaveChangesAsync();

            // Jumps straight to Delivered - this test is about the return's
            // side effect on standing, not the lifecycle that gets it there.
            await dbContext.Database.ExecuteSqlInterpolatedAsync(
                $"UPDATE orders SET status = {OrderStatuses.Delivered} WHERE id = {order.Id}");
            orderId = order.Id;
        }

        await using (var dbContext = new OrdersDbContext(_dbOptions))
        {
            var repository = new EfOrderReturnRepository(dbContext, _pipelineProvider);
            var order = await repository.FindForReturnAsync(orderId, CancellationToken.None);
            Assert.NotNull(order);

            var (orderReturn, rejection, _) = order!.TryReturn(
                [("SKU-FULL-RETURN-001", 1)], "customer return", ReturnReasonCategory.Unwanted,
                TimeSpan.FromDays(30), DateTimeOffset.UtcNow);
            Assert.Equal(ReturnRejectionReason.None, rejection);
            Assert.True(order.IsFullyReturned);

            await repository.SaveReturnAsync(order, orderReturn!, order.IsFullyReturned, "full-return-test", CancellationToken.None);
        }

        await using var assertDbContext = new OrdersDbContext(_dbOptions);
        var customerAfter = await assertDbContext.Customers.AsNoTracking().SingleAsync(item => item.Id == customerId);
        Assert.Equal(OrderStatuses.Returned, (await assertDbContext.Orders.AsNoTracking().SingleAsync(item => item.Id == orderId)).Status);
        Assert.Equal(0m, customerAfter.LifetimeSpend);
        Assert.Equal(0, customerAfter.CompletedOrderCount);
    }

    [Fact]
    public async Task APartialReturnDoesNotTouchCustomerStanding()
    {
        const string customerId = "partial-return-standing-customer";
        Guid orderId;

        await using (var dbContext = new OrdersDbContext(_dbOptions))
        {
            var customer = Customer.Create(customerId, DateTimeOffset.UtcNow.AddDays(-30));
            customer.RecordCompletedOrder(300m);
            dbContext.Customers.Add(customer);

            var order = Order.CreateWithLines(
                customerId,
                "BRL",
                DateTimeOffset.UtcNow,
                couponCode: null,
                [new OrderLineDraft("SKU-PARTIAL-RETURN-001", "Test Product", "test-category", 2, 150m, 0m, 0m)],
                discountTotal: 0m,
                shippingTotal: 0m,
                taxTotal: 0m,
                paymentMethod: PaymentMethods.Pix,
                shippingAddress: null);
            dbContext.Orders.Add(order);
            await dbContext.SaveChangesAsync();

            await dbContext.Database.ExecuteSqlInterpolatedAsync(
                $"UPDATE orders SET status = {OrderStatuses.Delivered} WHERE id = {order.Id}");
            orderId = order.Id;
        }

        await using (var dbContext = new OrdersDbContext(_dbOptions))
        {
            var repository = new EfOrderReturnRepository(dbContext, _pipelineProvider);
            var order = await repository.FindForReturnAsync(orderId, CancellationToken.None);
            Assert.NotNull(order);

            // Returns one of the two units - the order stays open for the rest, per Customer.ReverseCompletedOrder's own doc comment.
            var (orderReturn, rejection, _) = order!.TryReturn(
                [("SKU-PARTIAL-RETURN-001", 1)], "customer return", ReturnReasonCategory.Unwanted,
                TimeSpan.FromDays(30), DateTimeOffset.UtcNow);
            Assert.Equal(ReturnRejectionReason.None, rejection);
            Assert.False(order.IsFullyReturned);

            await repository.SaveReturnAsync(order, orderReturn!, order.IsFullyReturned, "partial-return-test", CancellationToken.None);
        }

        await using var assertDbContext = new OrdersDbContext(_dbOptions);
        var customerAfter = await assertDbContext.Customers.AsNoTracking().SingleAsync(item => item.Id == customerId);
        Assert.Equal(OrderStatuses.Delivered, (await assertDbContext.Orders.AsNoTracking().SingleAsync(item => item.Id == orderId)).Status);
        Assert.Equal(300m, customerAfter.LifetimeSpend);
        Assert.Equal(1, customerAfter.CompletedOrderCount);
    }
}
