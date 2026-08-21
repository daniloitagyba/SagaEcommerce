using BuildingBlocks;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Orders.Application;
using Orders.Application.Ports;
using Orders.Application.UseCases.ReturnOrder;
using Orders.Domain;

namespace Orders.UnitTests;

/// <summary>The ownership gate ReturnOrderHandler.HandleAsync applies before calling Order.TryReturn - the same "not yours reads as not found" rule AdvanceFulfillmentHandlerTests pins for self-service cancellation, applied here to returns.</summary>
public sealed class ReturnOrderHandlerTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 10, 12, 0, 0, TimeSpan.Zero);

    private static Order DeliveredOrderWithOneLine(string customerId, DateTimeOffset? createdAt = null)
    {
        var order = Order.CreateWithLines(
            customerId, "BRL", createdAt ?? Now.AddDays(-10), couponCode: null,
            [new OrderLineDraft("SKU-A", "Widget", "misc", 2, 50m, LineDiscount: 0m)],
            discountTotal: 0m, shippingTotal: 10m, taxTotal: 0m, paymentMethod: "Pix", shippingAddress: null);
        typeof(Order).GetProperty(nameof(Order.Status))!.SetValue(order, OrderStatuses.Delivered);
        return order;
    }

    private sealed class FakeOrderReturnRepository(Order? order) : IOrderReturnRepository
    {
        public bool SaveCalled { get; private set; }

        public Task<Order?> FindForReturnAsync(Guid orderId, CancellationToken cancellationToken) => Task.FromResult(order);

        public Task SaveReturnAsync(Order order, OrderReturn orderReturn, bool markOrderReturned, string correlationId, CancellationToken cancellationToken)
        {
            SaveCalled = true;
            return Task.CompletedTask;
        }
    }

    private sealed class RecordingOrderCache : IOrderCache
    {
        public int InvalidateCallCount { get; private set; }

        public Task<CacheLookup> GetOrCreateAsync(Guid id, Func<CancellationToken, Task<CachedOrder?>> factory, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Not used by ReturnOrderHandler.");

        public Task InvalidateAsync(Guid id, CancellationToken cancellationToken)
        {
            InvalidateCallCount++;
            return Task.CompletedTask;
        }
    }

    private static ReturnOrderHandler Handler(
        Order? order, RecordingOrderCache cache, out FakeOrderReturnRepository repository, TimeProvider? timeProvider = null)
    {
        repository = new FakeOrderReturnRepository(order);
        return new ReturnOrderHandler(
            repository, cache, Options.Create(new ReturnOptions()), timeProvider ?? TimeProvider.System, NullLogger<ReturnOrderHandler>.Instance);
    }

    [Fact]
    public async Task TheOwnerCanReturnTheirOwnOrder()
    {
        var order = DeliveredOrderWithOneLine("customer-1");
        var cache = new RecordingOrderCache();
        var handler = Handler(order, cache, out var repository);

        var result = await handler.HandleAsync(
            order.Id, [new ReturnItemRequest("SKU-A", 1)], "changed my mind", ReturnReasonCategory.Regret,
            new CallerIdentity("customer-1", IsAdmin: false), "correlation-1", CancellationToken.None);

        Assert.Equal(ReturnRejectionReason.None, result.Rejection);
        Assert.False(result.OrderNotFound);
        Assert.True(repository.SaveCalled);
        Assert.Equal(1, cache.InvalidateCallCount);
    }

    [Fact]
    public async Task ACallerCannotReturnSomeoneElsesOrderAndSeesNotFoundNotForbidden()
    {
        var order = DeliveredOrderWithOneLine("customer-1");
        var cache = new RecordingOrderCache();
        var handler = Handler(order, cache, out var repository);

        var result = await handler.HandleAsync(
            order.Id, [new ReturnItemRequest("SKU-A", 1)], "changed my mind", ReturnReasonCategory.Regret,
            new CallerIdentity("customer-2", IsAdmin: false), "correlation-1", CancellationToken.None);

        Assert.True(result.OrderNotFound);
        Assert.Equal(ReturnRejectionReason.None, result.Rejection);
        Assert.False(repository.SaveCalled);
        Assert.Equal(0, cache.InvalidateCallCount);
    }

    [Fact]
    public async Task AnAdminCanReturnAnyonesOrder()
    {
        var order = DeliveredOrderWithOneLine("customer-1");
        var cache = new RecordingOrderCache();
        var handler = Handler(order, cache, out var repository);

        var result = await handler.HandleAsync(
            order.Id, [new ReturnItemRequest("SKU-A", 1)], "changed my mind", ReturnReasonCategory.Regret,
            new CallerIdentity(CustomerId: null, IsAdmin: true), "correlation-1", CancellationToken.None);

        Assert.Equal(ReturnRejectionReason.None, result.Rejection);
        Assert.True(repository.SaveCalled);
    }

    [Fact]
    public async Task ANonexistentOrderIsReportedAsNotFound()
    {
        var cache = new RecordingOrderCache();
        var handler = Handler(null, cache, out var repository);

        var result = await handler.HandleAsync(
            Guid.NewGuid(), [new ReturnItemRequest("SKU-A", 1)], "changed my mind", ReturnReasonCategory.Regret,
            new CallerIdentity("customer-1", IsAdmin: false), "correlation-1", CancellationToken.None);

        Assert.True(result.OrderNotFound);
        Assert.False(repository.SaveCalled);
    }

    [Fact]
    public async Task ARejectedReturnIsNeverPersistedAndTheCacheIsNotInvalidated()
    {
        var order = DeliveredOrderWithOneLine("customer-1");
        var cache = new RecordingOrderCache();
        var handler = Handler(order, cache, out var repository);

        var result = await handler.HandleAsync(
            order.Id, [new ReturnItemRequest("SKU-A", 99)], "too many", ReturnReasonCategory.Regret,
            new CallerIdentity("customer-1", IsAdmin: false), "correlation-1", CancellationToken.None);

        Assert.Equal(ReturnRejectionReason.ExceedsPurchasedQuantity, result.Rejection);
        Assert.False(repository.SaveCalled);
        Assert.Equal(0, cache.InvalidateCallCount);
    }

    /// <summary>Regression: HandleAsync used DateTimeOffset.UtcNow directly instead of the injected TimeProvider, so fixing "now" in a test never controlled the regret-window comparison.</summary>
    [Fact]
    public async Task AFullyReturnedRegretRequestInsideTheWindowRefundsShipping()
    {
        var createdAt = Now.AddDays(-3);
        var order = DeliveredOrderWithOneLine("customer-1", createdAt);
        var cache = new RecordingOrderCache();
        var handler = Handler(order, cache, out _, new FakeTimeProvider(Now));

        var result = await handler.HandleAsync(
            order.Id, [new ReturnItemRequest("SKU-A", 2)], "changed my mind", ReturnReasonCategory.Regret,
            new CallerIdentity("customer-1", IsAdmin: false), "correlation-1", CancellationToken.None);

        Assert.Equal(ReturnRejectionReason.None, result.Rejection);
        Assert.True(result.OrderFullyReturned);
        Assert.Equal(10m, result.RefundTotal - GoodsRefundOnly(order));
    }

    /// <summary>Same full return, same reason, one day past the 7-day default window - shipping is not owed, per ShippingRefundPolicy.IsOwed.</summary>
    [Fact]
    public async Task AFullyReturnedRegretRequestOutsideTheWindowDoesNotRefundShipping()
    {
        var createdAt = Now.AddDays(-8);
        var order = DeliveredOrderWithOneLine("customer-1", createdAt);
        var cache = new RecordingOrderCache();
        var handler = Handler(order, cache, out _, new FakeTimeProvider(Now));

        var result = await handler.HandleAsync(
            order.Id, [new ReturnItemRequest("SKU-A", 2)], "changed my mind", ReturnReasonCategory.Regret,
            new CallerIdentity("customer-1", IsAdmin: false), "correlation-1", CancellationToken.None);

        Assert.Equal(ReturnRejectionReason.None, result.Rejection);
        Assert.True(result.OrderFullyReturned);
        Assert.Equal(0m, result.RefundTotal - GoodsRefundOnly(order));
    }

    /// <summary>The goods portion of a full return of DeliveredOrderWithOneLine's single line (LineDiscount and taxTotal are both 0 in that fixture, so this is the line's whole charged total), independent of whether shipping is owed - lets the two tests above assert the shipping difference without duplicating ReturnRefundTests' own coverage of the line-level arithmetic.</summary>
    private static decimal GoodsRefundOnly(Order order) =>
        order.Lines.Single().LineTotal;
}
