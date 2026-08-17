using System.Reflection;
using BuildingBlocks;
using Microsoft.Extensions.Logging.Abstractions;
using Orders.Application;
using Orders.Application.Ports;
using Orders.Application.UseCases.AdvanceFulfillment;
using Orders.Domain;

namespace Orders.UnitTests;

/// <summary>Covers HandleSelfServiceCancelAsync's ownership rule (owner or admin only, NotFound not Forbidden for non-owners) and its allowed-from-states list.</summary>
public sealed class AdvanceFulfillmentHandlerTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 10, 12, 0, 0, TimeSpan.Zero);

    /// <summary>Sets Order.Status via reflection, standing in for EF Core hydration from a non-Created row.</summary>
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
        Assert.Null(statusRepository.LastCall);
        Assert.Equal(0, cache.InvalidateCallCount);
    }

    [Fact]
    public async Task LosingARaceAgainstAConcurrentTransitionIsReportedAsNotApplicable()
    {
        var order = WithStatus(Order.Create("customer-1", 100m, "BRL", Now), OrderStatuses.Created);
        var statusRepository = new RecordingOrderStatusRepository(new OrderTransition(OrderTransitionOutcome.NotApplicable, null));
        var cache = new RecordingOrderCache();

        var result = await Handler(order, statusRepository, cache)
            .HandleSelfServiceCancelAsync(order.Id, new CallerIdentity("customer-1", IsAdmin: false), "correlation-1", CancellationToken.None);

        Assert.Equal(AdvanceFulfillmentOutcome.NotApplicable, result.Outcome);
        Assert.Equal(0, cache.InvalidateCallCount);
    }
}
