using BuildingBlocks;
using CsCheck;

namespace Orders.UnitTests;

/// <summary>
/// Milestone 69: the order lifecycle as a table. Before this, "which moves
/// are legal" wasn't answerable in code - each transition was a hardcoded
/// pair inside OrderStatusStore, which only worked while a status had one
/// legal predecessor, until the fulfilment states introduced more.
/// </summary>
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
    // Milestone 76: discovered only after the goods shipped - an expired-not-captured authorization.
    [InlineData(OrderStatuses.Shipped, OrderStatuses.FulfillmentHold)]
    // Milestone 74: parked here when the network can't cover it - cleared on restock (Confirmed) or given up on timeout (Cancelled).
    [InlineData(OrderStatuses.Created, OrderStatuses.Backordered)]
    [InlineData(OrderStatuses.Backordered, OrderStatuses.Confirmed)]
    [InlineData(OrderStatuses.Backordered, OrderStatuses.Cancelled)]
    public void TheHappyAndRecoveryPathsAreLegal(string from, string to)
    {
        Assert.True(OrderStatuses.CanTransition(from, to));
    }

    [Theory]
    // No skipping the warehouse.
    [InlineData(OrderStatuses.Confirmed, OrderStatuses.Shipped)]
    [InlineData(OrderStatuses.Confirmed, OrderStatuses.Delivered)]
    [InlineData(OrderStatuses.Created, OrderStatuses.Picking)]
    // No going backwards.
    [InlineData(OrderStatuses.Shipped, OrderStatuses.Picking)]
    [InlineData(OrderStatuses.Delivered, OrderStatuses.Shipped)]
    [InlineData(OrderStatuses.Confirmed, OrderStatuses.Created)]
    // Backordered is reached only from Created, only by the saga's own reservation reply.
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
        // Cancelling after dispatch would void a hold for goods already in a van - reversed by a *return*, not a cancellation.
        Assert.False(OrderStatuses.CanTransition(OrderStatuses.Shipped, OrderStatuses.Cancelled));
        Assert.False(OrderStatuses.CanTransition(OrderStatuses.Delivered, OrderStatuses.Cancelled));
    }

    [Fact]
    public void NothingEscapesATerminalState()
    {
        // Milestone 70 took Delivered off this list - a delivered order can still be returned, so it isn't the row's end of life.
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

        // Nothing that never arrived can come back.
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
        // Created is the state an order is born in, not one it can return to.
        Assert.Empty(OrderStatuses.PredecessorsOf(OrderStatuses.Created));
        Assert.DoesNotContain(OrderStatuses.Created, OrderStatuses.TransitionableTargets);
    }

    [Fact]
    public void OnlyShippingCapturesAndOnlyCancellingVoids()
    {
        Assert.Equal(OrderSettlementAction.Capture, OrderStatuses.SettlementActionFor(OrderStatuses.Shipped));
        Assert.Equal(OrderSettlementAction.Void, OrderStatuses.SettlementActionFor(OrderStatuses.Cancelled));

        // Milestone 68 captured at Confirmed because Shipped didn't exist - must not any more, the whole point of the hold.
        Assert.Equal(OrderSettlementAction.None, OrderStatuses.SettlementActionFor(OrderStatuses.Confirmed));
        Assert.Equal(OrderSettlementAction.None, OrderStatuses.SettlementActionFor(OrderStatuses.Picking));
        Assert.Equal(OrderSettlementAction.None, OrderStatuses.SettlementActionFor(OrderStatuses.Delivered));
        Assert.Equal(OrderSettlementAction.None, OrderStatuses.SettlementActionFor(OrderStatuses.FulfillmentHold));

        // No money has moved yet at Backordered - payment is decided one step later than reservation.
        Assert.Equal(OrderSettlementAction.None, OrderStatuses.SettlementActionFor(OrderStatuses.Backordered));
    }

    [Fact]
    public void EveryReachableStatusIsKnownAndEveryPredecessorIsReal()
    {
        // Guards the table against a typo'd status creating an unreachable state or a nonexistent predecessor.
        foreach (var target in OrderStatuses.TransitionableTargets)
        {
            Assert.True(OrderStatuses.IsKnown(target), $"'{target}' is a transition target but not a known status.");
            Assert.All(
                OrderStatuses.PredecessorsOf(target),
                predecessor => Assert.True(OrderStatuses.IsKnown(predecessor), $"'{predecessor}' is a predecessor but not a known status."));
        }
    }

    [Fact]
    public void MoneyIsOnlyEverAskedToBeCapturedOnceAndVoidedOnceAlongAnyPath()
    {
        // Capture only ever fires at Shipped, void only ever fires at
        // Cancelled - neither status is reachable twice in one path
        // (Shipped has one outgoing edge, to Delivered; Cancelled is
        // terminal), so each action still fires at most once.
        //
        // Milestone 76 made Shipped -> FulfillmentHold -> Cancelled legal
        // (a shipped order whose capture actually failed, reconciled into
        // a hold, then cancelled by an operator) - which means the graph
        // can now legally ask for both a capture and a void on the same
        // order. That's safe only because Payment.TryCapture/
        // TrySettleWithoutCapture guard on the payment's *actual* state,
        // not the order's: a capture that never truly happened (payment
        // sitting in Expired) means the later void is a no-op at the
        // domain level, not a second real settlement. This test can no
        // longer prove "never both" by itself - that guarantee now lives
        // one layer down, in Payment's own state guard (see
        // PaymentAuthorizationTests).
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
                var captures = 0;
                var voids = 0;

                foreach (var next in path)
                {
                    if (!OrderStatuses.CanTransition(current, next))
                    {
                        continue;
                    }

                    switch (OrderStatuses.SettlementActionFor(next))
                    {
                        case OrderSettlementAction.Capture: captures++; break;
                        case OrderSettlementAction.Void: voids++; break;
                    }

                    current = next;
                }

                return captures <= 1 && voids <= 1;
            },
            iter: 10_000);
    }
}
