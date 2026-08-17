using BuildingBlocks;
using CsCheck;

namespace Orders.UnitTests;

/// <summary>The order lifecycle as a table, replacing the hardcoded transition pairs inside OrderStatusStore that only worked while a status had one legal predecessor.</summary>
public class OrderStatusTransitionTests
{
    [Theory]
    [InlineData(OrderStatuses.Created, OrderStatuses.Confirmed)]
    [InlineData(OrderStatuses.Created, OrderStatuses.Cancelled)]
    [InlineData(OrderStatuses.Confirmed, OrderStatuses.Picking)]
    [InlineData(OrderStatuses.Confirmed, OrderStatuses.Cancelled)]
    [InlineData(OrderStatuses.Confirmed, OrderStatuses.FulfillmentHold)]
    [InlineData(OrderStatuses.Picking, OrderStatuses.Shipped)]
    [InlineData(OrderStatuses.Picking, OrderStatuses.Cancelled)]
    [InlineData(OrderStatuses.Shipped, OrderStatuses.Delivered)]
    [InlineData(OrderStatuses.FulfillmentHold, OrderStatuses.Picking)]
    [InlineData(OrderStatuses.FulfillmentHold, OrderStatuses.Cancelled)]
    [InlineData(OrderStatuses.Shipped, OrderStatuses.FulfillmentHold)]
    [InlineData(OrderStatuses.Created, OrderStatuses.Backordered)]
    [InlineData(OrderStatuses.Backordered, OrderStatuses.Confirmed)]
    [InlineData(OrderStatuses.Backordered, OrderStatuses.Cancelled)]
    public void TheHappyAndRecoveryPathsAreLegal(string from, string to)
    {
        Assert.True(OrderStatuses.CanTransition(from, to));
    }

    [Theory]
    [InlineData(OrderStatuses.Confirmed, OrderStatuses.Shipped)]
    [InlineData(OrderStatuses.Confirmed, OrderStatuses.Delivered)]
    [InlineData(OrderStatuses.Created, OrderStatuses.Picking)]
    [InlineData(OrderStatuses.Shipped, OrderStatuses.Picking)]
    [InlineData(OrderStatuses.Delivered, OrderStatuses.Shipped)]
    [InlineData(OrderStatuses.Confirmed, OrderStatuses.Created)]
    [InlineData(OrderStatuses.Confirmed, OrderStatuses.Backordered)]
    [InlineData(OrderStatuses.Backordered, OrderStatuses.Picking)]
    [InlineData(OrderStatuses.Backordered, OrderStatuses.Shipped)]
    public void IllegalMovesAreRefused(string from, string to)
    {
        Assert.False(OrderStatuses.CanTransition(from, to));
    }

    [Fact]
    public void AShippedOrderCannotBeCancelled()
    {
        Assert.False(OrderStatuses.CanTransition(OrderStatuses.Shipped, OrderStatuses.Cancelled));
        Assert.False(OrderStatuses.CanTransition(OrderStatuses.Delivered, OrderStatuses.Cancelled));
    }

    [Fact]
    public void NothingEscapesATerminalState()
    {
        foreach (var terminal in new[] { OrderStatuses.Cancelled, OrderStatuses.Returned })
        {
            Assert.True(OrderStatuses.IsTerminal(terminal));
            Assert.All(
                OrderStatuses.TransitionableTargets,
                target => Assert.False(OrderStatuses.CanTransition(terminal, target)));
        }

        Assert.False(OrderStatuses.IsTerminal(OrderStatuses.Delivered));
    }

    [Fact]
    public void OnlyADeliveredOrderCanBeReturned()
    {
        Assert.True(OrderStatuses.CanTransition(OrderStatuses.Delivered, OrderStatuses.Returned));

        foreach (var notDelivered in new[]
                 {
                     OrderStatuses.Created, OrderStatuses.Confirmed, OrderStatuses.Picking,
                     OrderStatuses.Shipped, OrderStatuses.Cancelled, OrderStatuses.FulfillmentHold,
                     OrderStatuses.Backordered
                 })
        {
            Assert.False(OrderStatuses.CanTransition(notDelivered, OrderStatuses.Returned));
        }
    }

    [Fact]
    public void CreatedIsOnlyEverSetAtConstructionNeverTransitionedInto()
    {
        Assert.Empty(OrderStatuses.PredecessorsOf(OrderStatuses.Created));
        Assert.DoesNotContain(OrderStatuses.Created, OrderStatuses.TransitionableTargets);
    }

    [Fact]
    public void OnlyShippingCapturesAndOnlyCancellingCancels()
    {
        Assert.Equal(OrderSettlementAction.Capture, OrderStatuses.SettlementActionFor(OrderStatuses.Shipped));
        Assert.Equal(OrderSettlementAction.Cancel, OrderStatuses.SettlementActionFor(OrderStatuses.Cancelled));

        Assert.Equal(OrderSettlementAction.None, OrderStatuses.SettlementActionFor(OrderStatuses.Confirmed));
        Assert.Equal(OrderSettlementAction.None, OrderStatuses.SettlementActionFor(OrderStatuses.Picking));
        Assert.Equal(OrderSettlementAction.None, OrderStatuses.SettlementActionFor(OrderStatuses.Delivered));
        Assert.Equal(OrderSettlementAction.None, OrderStatuses.SettlementActionFor(OrderStatuses.FulfillmentHold));

        Assert.Equal(OrderSettlementAction.None, OrderStatuses.SettlementActionFor(OrderStatuses.Backordered));
    }

    [Fact]
    public void EveryReachableStatusIsKnownAndEveryPredecessorIsReal()
    {
        foreach (var target in OrderStatuses.TransitionableTargets)
        {
            Assert.True(OrderStatuses.IsKnown(target), $"'{target}' is a transition target but not a known status.");
            Assert.All(
                OrderStatuses.PredecessorsOf(target),
                predecessor => Assert.True(OrderStatuses.IsKnown(predecessor), $"'{predecessor}' is a predecessor but not a known status."));
        }
    }

    [Fact]
    public void FulfillmentDrivableTargetsExcludesTheStatusesOnlyAnAggregateOrTheSagaMayReach()
    {
        Assert.DoesNotContain(OrderStatuses.Confirmed, OrderStatuses.FulfillmentDrivableTargets);
        Assert.DoesNotContain(OrderStatuses.Backordered, OrderStatuses.FulfillmentDrivableTargets);
        Assert.DoesNotContain(OrderStatuses.Returned, OrderStatuses.FulfillmentDrivableTargets);

        Assert.All(
            OrderStatuses.FulfillmentDrivableTargets,
            target => Assert.Contains(target, OrderStatuses.TransitionableTargets));

        Assert.Equal(
            new[] { OrderStatuses.Picking, OrderStatuses.Shipped, OrderStatuses.Delivered, OrderStatuses.FulfillmentHold, OrderStatuses.Cancelled },
            OrderStatuses.FulfillmentDrivableTargets);
    }

    [Fact]
    public void CancelIsRequestedAtMostOnceButRepeatedCaptureRequestsAreSafeOnlyBecausePaymentItselfIsIdempotent()
    {
        var allStatuses = new[]
        {
            OrderStatuses.Confirmed, OrderStatuses.Picking, OrderStatuses.Shipped,
            OrderStatuses.Delivered, OrderStatuses.Cancelled, OrderStatuses.FulfillmentHold,
            OrderStatuses.Backordered
        };

        Gen.OneOfConst(allStatuses).List[1, 8].Sample(
            path =>
            {
                var current = OrderStatuses.Created;
                var cancellations = 0;

                foreach (var next in path)
                {
                    if (!OrderStatuses.CanTransition(current, next))
                    {
                        continue;
                    }

                    if (OrderStatuses.SettlementActionFor(next) == OrderSettlementAction.Cancel)
                    {
                        cancellations++;
                    }

                    current = next;
                }

                return cancellations <= 1;
            },
            iter: 10_000);
    }
}
