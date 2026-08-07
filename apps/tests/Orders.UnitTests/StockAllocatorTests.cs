using CsCheck;
using Inventory.Service.Domain;

namespace Orders.UnitTests;

/// <summary>
/// Milestone 72: the multi-warehouse allocation policy.
///
/// The policy is the interesting part of splitting stock across buildings -
/// not the extra table - so it is a pure function, which is what makes it
/// property-testable at all. The properties below are the ones that must
/// hold for any stock configuration, because getting them wrong means
/// either overselling or shipping an order in more parcels than it needed.
/// </summary>
public class StockAllocatorTests
{
    private static StockAllocator.Candidate Warehouse(string code, int available, int priority = 1) =>
        new(code, available, priority);

    [Fact]
    public void OneWarehouseThatCanCoverTheOrderShipsItWhole()
    {
        // Both hold enough and share a priority, so the tie breaks on the
        // warehouse code - "WH-RJ" before "WH-SP". Asserting WH-SP because
        // it happens to be listed first is exactly the input-order
        // dependence AllocationIsDeterministicRegardlessOfInputOrder
        // forbids, and this test originally made that mistake.
        var plan = StockAllocator.Allocate([Warehouse("WH-SP", 10), Warehouse("WH-RJ", 10)], 4);

        Assert.True(plan.Fulfillable);
        var line = Assert.Single(plan.Lines);
        Assert.Equal("WH-RJ", line.WarehouseCode);
        Assert.Equal(4, line.Quantity);
    }

    [Fact]
    public void PriorityDecidesWhichSingleWarehouseNotSize()
    {
        // WH-RJ holds more, but WH-SP has the better priority and can still
        // cover the order - splitting to the bigger pile would be a worse
        // outcome for no reason.
        var plan = StockAllocator.Allocate(
            [Warehouse("WH-RJ", 100, priority: 2), Warehouse("WH-SP", 10, priority: 1)], 5);

        var line = Assert.Single(plan.Lines);
        Assert.Equal("WH-SP", line.WarehouseCode);
    }

    [Fact]
    public void ItSplitsOnlyWhenNoSingleWarehouseCanCoverTheOrder()
    {
        var plan = StockAllocator.Allocate(
            [Warehouse("WH-SP", 3, priority: 1), Warehouse("WH-RJ", 4, priority: 2)], 6);

        Assert.True(plan.Fulfillable);
        Assert.Equal(2, plan.Lines.Count);
        Assert.Equal(("WH-SP", 3), (plan.Lines[0].WarehouseCode, plan.Lines[0].Quantity));
        Assert.Equal(("WH-RJ", 3), (plan.Lines[1].WarehouseCode, plan.Lines[1].Quantity));
    }

    [Fact]
    public void NotEnoughAnywhereIsRefusedRatherThanPartiallyAllocated()
    {
        // All-or-nothing on purpose: the saga's reservation step confirms an
        // order, and a partial reservation would confirm one the warehouse
        // cannot actually fill.
        var plan = StockAllocator.Allocate([Warehouse("WH-SP", 2), Warehouse("WH-RJ", 3)], 6);

        Assert.False(plan.Fulfillable);
        Assert.Empty(plan.Lines);
    }

    [Fact]
    public void EmptyWarehousesAreIgnoredAndAnEmptyNetworkIsRefused()
    {
        var plan = StockAllocator.Allocate([Warehouse("WH-SP", 0), Warehouse("WH-RJ", 5)], 5);
        Assert.Equal("WH-RJ", Assert.Single(plan.Lines).WarehouseCode);

        Assert.False(StockAllocator.Allocate([], 1).Fulfillable);
        Assert.False(StockAllocator.Allocate([Warehouse("WH-SP", 5)], 0).Fulfillable);
    }

    [Fact]
    public void AllocationIsDeterministicRegardlessOfInputOrder()
    {
        // Two replicas reasoning about the same stock must reach the same
        // plan, so ties may never be broken by whatever order the rows came
        // back in.
        var forwards = StockAllocator.Allocate(
            [Warehouse("WH-A", 3, 1), Warehouse("WH-B", 3, 1), Warehouse("WH-C", 3, 1)], 7);
        var backwards = StockAllocator.Allocate(
            [Warehouse("WH-C", 3, 1), Warehouse("WH-B", 3, 1), Warehouse("WH-A", 3, 1)], 7);

        Assert.Equal(
            forwards.Lines.Select(line => (line.WarehouseCode, line.Quantity)),
            backwards.Lines.Select(line => (line.WarehouseCode, line.Quantity)));
    }

    [Fact]
    public void APlanNeverAllocatesMoreThanAWarehouseHasAndAlwaysSumsToTheRequest()
    {
        // The two properties that prevent overselling: no warehouse is ever
        // asked for more than it holds, and the plan covers exactly what was
        // requested - never less (a short shipment) and never more (stock
        // conjured from nowhere).
        var gen =
            from stocks in Gen.Int[0, 20].Array[1, 5]
            from requested in Gen.Int[1, 60]
            select (stocks, requested);

        gen.Sample(
            input =>
            {
                var candidates = input.stocks
                    .Select((available, index) => Warehouse($"WH-{index:00}", available, index))
                    .ToList();

                var plan = StockAllocator.Allocate(candidates, input.requested);

                if (!plan.Fulfillable)
                {
                    // Refusal is only legitimate when the network genuinely
                    // cannot cover the request.
                    return input.stocks.Sum() < input.requested;
                }

                if (plan.Lines.Sum(line => line.Quantity) != input.requested)
                {
                    return false;
                }

                // No warehouse over-allocated, and none named twice.
                var byWarehouse = plan.Lines
                    .GroupBy(line => line.WarehouseCode, StringComparer.Ordinal)
                    .ToDictionary(group => group.Key, group => group.Sum(line => line.Quantity), StringComparer.Ordinal);

                if (byWarehouse.Count != plan.Lines.Count)
                {
                    return false;
                }

                return byWarehouse.All(entry =>
                    entry.Value > 0
                    && entry.Value <= candidates.Single(c => c.WarehouseCode == entry.Key).Available);
            },
            iter: 10_000);
    }

    [Fact]
    public void ItNeverSplitsWhenASingleWarehouseCouldHaveCoveredTheOrder()
    {
        // Splitting means two parcels, two shipping costs and two chances
        // for a leg to go missing. It has to be a fallback, never a habit.
        var gen =
            from stocks in Gen.Int[0, 20].Array[1, 5]
            from requested in Gen.Int[1, 20]
            select (stocks, requested);

        gen.Sample(
            input =>
            {
                var candidates = input.stocks
                    .Select((available, index) => Warehouse($"WH-{index:00}", available, index))
                    .ToList();

                var plan = StockAllocator.Allocate(candidates, input.requested);
                if (!plan.Fulfillable)
                {
                    return true;
                }

                var anySingleCouldCover = candidates.Any(c => c.Available >= input.requested);
                return !anySingleCouldCover || plan.Lines.Count == 1;
            },
            iter: 10_000);
    }

    [Fact]
    public void ReorderPointFlagsAWarehouseThatNeedsReplenishing()
    {
        var now = DateTimeOffset.UtcNow;
        var stock = WarehouseStock.Create("SKU-1", "WH-SP", available: 10, reorderPoint: 5, now);

        Assert.False(stock.NeedsReplenishment);

        Assert.True(stock.TryReserve(5, now));
        Assert.True(stock.NeedsReplenishment);
        Assert.Equal(5, stock.AvailableQuantity);
        Assert.Equal(5, stock.ReservedQuantity);
    }

    [Fact]
    public void PerWarehouseStockCannotBeOverReservedOrOverReleased()
    {
        var now = DateTimeOffset.UtcNow;
        var stock = WarehouseStock.Create("SKU-1", "WH-SP", available: 3, reorderPoint: 0, now);

        Assert.False(stock.TryReserve(4, now));
        Assert.True(stock.TryReserve(3, now));
        Assert.False(stock.TryReserve(1, now));

        Assert.False(stock.TryRelease(4, now));
        Assert.True(stock.TryRelease(3, now));
        Assert.False(stock.TryCommit(1, now));
        Assert.Equal(3, stock.AvailableQuantity);
    }

    [Fact]
    public void ReplenishmentIsSignalledOnTheCrossingNotOnEveryLowReservation()
    {
        // Milestone 73. The reservation that takes a warehouse below its
        // reorder point is news; the next twenty orders finding it already
        // low are not. Emitting on every one is how a useful signal becomes
        // a topic everyone filters out, so the crossing is what the
        // allocation store samples for.
        var now = DateTimeOffset.UtcNow;
        var stock = WarehouseStock.Create("SKU-1", "WH-SP", available: 10, reorderPoint: 5, now);

        var wasStockedBefore = !stock.NeedsReplenishment;
        Assert.True(stock.TryReserve(4, now));
        Assert.True(wasStockedBefore && !stock.NeedsReplenishment);   // 6 left: no crossing yet

        wasStockedBefore = !stock.NeedsReplenishment;
        Assert.True(stock.TryReserve(2, now));
        Assert.True(wasStockedBefore && stock.NeedsReplenishment);    // 4 left: this is the crossing

        wasStockedBefore = !stock.NeedsReplenishment;
        Assert.True(stock.TryReserve(1, now));
        Assert.False(wasStockedBefore);                               // already low: silent
    }
}
