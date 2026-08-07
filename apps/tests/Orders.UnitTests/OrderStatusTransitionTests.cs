using BuildingBlocks;
using CsCheck;

namespace Orders.UnitTests;

/// <summary>
/// Milestone 69: the order lifecycle as a table.
///
/// Before this, "which moves are legal" was not a question the code could
/// answer - each transition was a hardcoded pair inside OrderStatusStore,
/// and the CAS's <c>WHERE status = @expected</c> answered the narrower
/// "is it in exactly this state?". The difference only shows up once a
/// status has more than one legal predecessor, which is exactly what the
/// fulfilment states introduce.
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
    // Milestone 74: the saga parks an order here when the network cannot
    // cover it, and can either clear it (Confirmed, once a restock arrives)
    // or give up on it (Cancelled, on timeout).
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
    // Backordered is reached only from Created, and only the saga's own
    // reservation reply puts an order there - not the fulfilment states.
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
        // The one that costs real money if it is wrong: cancelling after
        // dispatch would void an authorization for goods already in a van.
        // A shipped order is reversed by a *return*, not a cancellation.
        Assert.False(OrderStatuses.CanTransition(OrderStatuses.Shipped, OrderStatuses.Cancelled));
        Assert.False(OrderStatuses.CanTransition(OrderStatuses.Delivered, OrderStatuses.Cancelled));
    }

    [Fact]
    public void NothingEscapesATerminalState()
    {
        // Milestone 70 took Delivered off this list: it is the happy ending,
        // but a delivered order can still be returned, so it is not the end
        // of the row's life. Cancelled and Returned are.
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

        // Nothing that never arrived can come back, and a cancelled order
        // was never charged in the first place.
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
        // Nothing may move an order back to Created - it is the state an
        // order is born in, not one it can return to.
        Assert.Empty(OrderStatuses.PredecessorsOf(OrderStatuses.Created));
        Assert.DoesNotContain(OrderStatuses.Created, OrderStatuses.TransitionableTargets);
    }

    [Fact]
    public void OnlyShippingCapturesAndOnlyCancellingVoids()
    {
        Assert.Equal(OrderSettlementAction.Capture, OrderStatuses.SettlementActionFor(OrderStatuses.Shipped));
        Assert.Equal(OrderSettlementAction.Void, OrderStatuses.SettlementActionFor(OrderStatuses.Cancelled));

        // Milestone 68 captured at Confirmed because Shipped did not exist.
        // It must not any more - that is the whole point of the hold.
        Assert.Equal(OrderSettlementAction.None, OrderStatuses.SettlementActionFor(OrderStatuses.Confirmed));
        Assert.Equal(OrderSettlementAction.None, OrderStatuses.SettlementActionFor(OrderStatuses.Picking));
        Assert.Equal(OrderSettlementAction.None, OrderStatuses.SettlementActionFor(OrderStatuses.Delivered));
        Assert.Equal(OrderSettlementAction.None, OrderStatuses.SettlementActionFor(OrderStatuses.FulfillmentHold));

        // No money has moved yet at Backordered - payment is decided one
        // saga step later than reservation - so there is nothing to void.
        Assert.Equal(OrderSettlementAction.None, OrderStatuses.SettlementActionFor(OrderStatuses.Backordered));
    }

    [Fact]
    public void EveryReachableStatusIsKnownAndEveryPredecessorIsReal()
    {
        // Guards the table against a typo'd status silently creating an
        // unreachable state or a predecessor that does not exist.
        foreach (var target in OrderStatuses.TransitionableTargets)
        {
            Assert.True(OrderStatuses.IsKnown(target), $"'{target}' is a transition target but not a known status.");
            Assert.All(
                OrderStatuses.PredecessorsOf(target),
                predecessor => Assert.True(OrderStatuses.IsKnown(predecessor), $"'{predecessor}' is a predecessor but not a known status."));
        }
    }

    [Fact]
    public void MoneyIsOnlyEverSettledOnceAlongAnyPath()
    {
        // The property that matters across the whole lifecycle: walking any
        // legal sequence of transitions must never ask to capture and void
        // the same order, nor do either twice. Payment.TryCapture guards
        // this at the domain level too, but a lifecycle that *asks* for
        // both is a design error the guard would merely hide.
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

                return captures <= 1 && voids <= 1 && !(captures > 0 && voids > 0);
            },
            iter: 10_000);
    }
}
