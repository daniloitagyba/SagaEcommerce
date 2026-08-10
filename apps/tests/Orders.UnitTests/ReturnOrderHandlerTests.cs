using BuildingBlocks;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Orders.Application;
using Orders.Application.Ports;
using Orders.Application.UseCases.ReturnOrder;
using Orders.Domain;

namespace Orders.UnitTests;

/// <summary>
/// The ownership gate ReturnOrderHandler.HandleAsync applies
/// before ever calling into Order.TryReturn (which ReturnRefundTests
/// already covers exhaustively) had no test coverage anywhere - the same
/// "not yours reads as not found" rule AdvanceFulfillmentHandlerTests pins
/// for self-service cancellation, applied here to returns.
/// </summary>
public sealed class ReturnOrderHandlerTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 10, 12, 0, 0, TimeSpan.Zero);

    private static Order DeliveredOrderWithOneLine(string customerId)
    {
        var order = Order.CreateWithLines(
            customerId, "BRL", Now.AddDays(-10), couponCode: null,
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

    private static ReturnOrderHandler Handler(Order? order, RecordingOrderCache cache, out FakeOrderReturnRepository repository)
    {
        repository = new FakeOrderReturnRepository(order);
        return new ReturnOrderHandler(repository, cache, Options.Create(new ReturnOptions()), NullLogger<ReturnOrderHandler>.Instance);
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
        // TryReturn's own rejection reasons are ReturnRefundTests' territory -
        // this just pins that the handler actually respects a rejection
        // rather than saving/invalidating regardless.
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
}
