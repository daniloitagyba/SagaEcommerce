using System.Reflection;
using BuildingBlocks;
using Microsoft.Extensions.Logging.Abstractions;
using Orders.Application;
using Orders.Application.Ports;
using Orders.Application.UseCases.AdvanceFulfillment;
using Orders.Domain;

namespace Orders.UnitTests;

/// <summary>
/// HandleSelfServiceCancelAsync - the shopper-facing self-cancel
/// route - had no test coverage anywhere in the suite despite carrying two
/// genuine security/business rules: a caller can only cancel their own
/// order (an admin can cancel anyone's), and "not yours" is indistinguishable
/// from "doesn't exist" to the caller, so a shopper probing order ids
/// learns nothing about ones that aren't theirs. The allowed-from-states
/// list (Created/Confirmed/Backordered) is enforced here too, deliberately
/// excluding Picking/FulfillmentHold - see the handler's own comment.
/// </summary>
public sealed class AdvanceFulfillmentHandlerTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 10, 12, 0, 0, TimeSpan.Zero);

    /// <summary>
    /// Order.Create/CreateWithLines always start an order at "Created" -
    /// deliberately: every real status change after that goes through
    /// IOrderStatusRepository's atomic compare-and-set, never back through
    /// the aggregate. A test double for that repository needs orders at
    /// other statuses too, the same way a real EF Core hydration would
    /// materialize one straight from a database row - reflection onto the
    /// private setter is what stands in for that here.
    /// </summary>
    private static Order WithStatus(Order order, string status)
    {
        typeof(Order).GetProperty(nameof(Order.Status))!.SetValue(order, status);
        return order;
    }

    private sealed class FakeOrderRepository(Order? order) : IOrderRepository
    {
        public Task<Order?> FindByIdAsync(Guid id, CancellationToken cancellationToken) => Task.FromResult(order);
    }

    private sealed class RecordingOrderStatusRepository(OrderTransition response) : IOrderStatusRepository
    {
        public (Guid OrderId, string TargetStatus, IReadOnlyList<string> AllowedFrom)? LastCall { get; private set; }

        public Task<OrderTransition> TryTransitionAsync(
            Guid orderId, string targetStatus, IReadOnlyList<string> allowedFrom,
            OrderSettlementAction settlementAction, string correlationId, CancellationToken cancellationToken)
        {
            LastCall = (orderId, targetStatus, allowedFrom);
            return Task.FromResult(response);
        }
    }

    private sealed class RecordingOrderCache : IOrderCache
    {
        public int InvalidateCallCount { get; private set; }

        public Task<CacheLookup> GetOrCreateAsync(Guid id, Func<CancellationToken, Task<CachedOrder?>> factory, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Not used by AdvanceFulfillmentHandler.");

        public Task InvalidateAsync(Guid id, CancellationToken cancellationToken)
        {
            InvalidateCallCount++;
            return Task.CompletedTask;
        }
    }

    private static AdvanceFulfillmentHandler Handler(
        Order? order, RecordingOrderStatusRepository statusRepository, RecordingOrderCache cache) =>
        new(statusRepository, new FakeOrderRepository(order), cache, NullLogger<AdvanceFulfillmentHandler>.Instance);

    [Fact]
    public async Task TheOwnerCanCancelTheirOwnOrder()
    {
        var order = WithStatus(Order.Create("customer-1", 100m, "BRL", Now), OrderStatuses.Created);
        var statusRepository = new RecordingOrderStatusRepository(new OrderTransition(OrderTransitionOutcome.Advanced, null));
        var cache = new RecordingOrderCache();

        var result = await Handler(order, statusRepository, cache)
            .HandleSelfServiceCancelAsync(order.Id, new CallerIdentity("customer-1", IsAdmin: false), "correlation-1", CancellationToken.None);

        Assert.Equal(AdvanceFulfillmentOutcome.Advanced, result.Outcome);
        Assert.Equal(OrderStatuses.Cancelled, result.Status);
        Assert.Equal(1, cache.InvalidateCallCount);
    }

    [Fact]
    public async Task ACallerCannotCancelSomeoneElsesOrderAndSeesNotFoundNotForbidden()
    {
        // Deliberately NotFound, not a 403-shaped outcome - see the handler's own comment on why.
        var order = WithStatus(Order.Create("customer-1", 100m, "BRL", Now), OrderStatuses.Created);
        var statusRepository = new RecordingOrderStatusRepository(new OrderTransition(OrderTransitionOutcome.Advanced, null));
        var cache = new RecordingOrderCache();

        var result = await Handler(order, statusRepository, cache)
            .HandleSelfServiceCancelAsync(order.Id, new CallerIdentity("customer-2", IsAdmin: false), "correlation-1", CancellationToken.None);

        Assert.Equal(AdvanceFulfillmentOutcome.NotFound, result.Outcome);
        Assert.Null(statusRepository.LastCall);
        Assert.Equal(0, cache.InvalidateCallCount);
    }

    [Fact]
    public async Task AnAdminCanCancelAnyonesOrder()
    {
        var order = WithStatus(Order.Create("customer-1", 100m, "BRL", Now), OrderStatuses.Confirmed);
        var statusRepository = new RecordingOrderStatusRepository(new OrderTransition(OrderTransitionOutcome.Advanced, null));
        var cache = new RecordingOrderCache();

        var result = await Handler(order, statusRepository, cache)
            .HandleSelfServiceCancelAsync(order.Id, new CallerIdentity(CustomerId: null, IsAdmin: true), "correlation-1", CancellationToken.None);

        Assert.Equal(AdvanceFulfillmentOutcome.Advanced, result.Outcome);
    }

    [Fact]
    public async Task ANonexistentOrderIsNotFound()
    {
        var statusRepository = new RecordingOrderStatusRepository(new OrderTransition(OrderTransitionOutcome.Advanced, null));
        var cache = new RecordingOrderCache();

        var result = await Handler(null, statusRepository, cache)
            .HandleSelfServiceCancelAsync(Guid.NewGuid(), new CallerIdentity("customer-1", IsAdmin: false), "correlation-1", CancellationToken.None);

        Assert.Equal(AdvanceFulfillmentOutcome.NotFound, result.Outcome);
    }

    [Theory]
    [InlineData(OrderStatuses.Created)]
    [InlineData(OrderStatuses.Confirmed)]
    [InlineData(OrderStatuses.Backordered)]
    public async Task OrdersInAnyOfTheThreeSelfServiceStatusesCanBeCancelled(string status)
    {
        var order = WithStatus(Order.Create("customer-1", 100m, "BRL", Now), status);
        var statusRepository = new RecordingOrderStatusRepository(new OrderTransition(OrderTransitionOutcome.Advanced, null));
        var cache = new RecordingOrderCache();

        var result = await Handler(order, statusRepository, cache)
            .HandleSelfServiceCancelAsync(order.Id, new CallerIdentity("customer-1", IsAdmin: false), "correlation-1", CancellationToken.None);

        Assert.Equal(AdvanceFulfillmentOutcome.Advanced, result.Outcome);
        Assert.Equal([OrderStatuses.Created, OrderStatuses.Confirmed, OrderStatuses.Backordered], statusRepository.LastCall!.Value.AllowedFrom);
    }

    // Picking/FulfillmentHold are deliberately excluded: the warehouse is
    // already acting, or a human already needs to look at it - see the
    // handler's own comment on SelfServiceCancellableFrom.
    [Theory]
    [InlineData(OrderStatuses.Picking)]
    [InlineData(OrderStatuses.FulfillmentHold)]
    [InlineData(OrderStatuses.Shipped)]
    [InlineData(OrderStatuses.Delivered)]
    [InlineData(OrderStatuses.Cancelled)]
    public async Task OrdersOutsideTheSelfServiceStatusesAreRefusedWithoutTouchingTheRepository(string status)
    {
        var order = WithStatus(Order.Create("customer-1", 100m, "BRL", Now), status);
        var statusRepository = new RecordingOrderStatusRepository(new OrderTransition(OrderTransitionOutcome.Advanced, null));
        var cache = new RecordingOrderCache();

        var result = await Handler(order, statusRepository, cache)
            .HandleSelfServiceCancelAsync(order.Id, new CallerIdentity("customer-1", IsAdmin: false), "correlation-1", CancellationToken.None);

        Assert.Equal(AdvanceFulfillmentOutcome.NotApplicable, result.Outcome);
        // The illegality is decided before ever calling the repository - no wasted/racy compare-and-set attempt.
        Assert.Null(statusRepository.LastCall);
        Assert.Equal(0, cache.InvalidateCallCount);
    }

    [Fact]
    public async Task LosingARaceAgainstAConcurrentTransitionIsReportedAsNotApplicable()
    {
        // The order looked cancellable when read, but something else (e.g. the saga confirming it) moved it first.
        var order = WithStatus(Order.Create("customer-1", 100m, "BRL", Now), OrderStatuses.Created);
        var statusRepository = new RecordingOrderStatusRepository(new OrderTransition(OrderTransitionOutcome.NotApplicable, null));
        var cache = new RecordingOrderCache();

        var result = await Handler(order, statusRepository, cache)
            .HandleSelfServiceCancelAsync(order.Id, new CallerIdentity("customer-1", IsAdmin: false), "correlation-1", CancellationToken.None);

        Assert.Equal(AdvanceFulfillmentOutcome.NotApplicable, result.Outcome);
        Assert.Equal(0, cache.InvalidateCallCount);
    }
}
