using CsCheck;
using Inventory.Service.Domain;

namespace Orders.UnitTests;

/// <summary>
/// Milestone 72: the multi-warehouse allocation policy - a pure function,
/// which is what makes it property-testable. These properties must hold
/// for any stock configuration; getting them wrong means overselling or
/// shipping in more parcels than needed.
/// </summary>
public class StockAllocatorTests
{
    private static StockAllocator.Candidate Warehouse(string code, int available, int priority = 1) =>
        new(code, available, priority);

    [Fact]
    public void OneWarehouseThatCanCoverTheOrderShipsItWhole()
    {
        // Both hold enough and share a priority, so the tie breaks on warehouse code - "WH-RJ" before "WH-SP", not input order.
        var plan = StockAllocator.Allocate([Warehouse("WH-SP", 10), Warehouse("WH-RJ", 10)], 4);

        Assert.True(plan.Fulfillable);
        var line = Assert.Single(plan.Lines);
        Assert.Equal("WH-RJ", line.WarehouseCode);
        Assert.Equal(4, line.Quantity);
    }

    [Fact]
    public void PriorityDecidesWhichSingleWarehouseNotSize()
    {
        // WH-RJ holds more, but WH-SP has the better priority and can still cover the order.
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
        // All-or-nothing: a partial reservation would confirm an order the warehouse can't actually fill.
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
        // Two replicas must reach the same plan; ties can't depend on row order.
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
        // Prevents overselling: no warehouse is asked for more than it holds, and the plan covers exactly what was requested.
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
                    // Refusal is only legitimate when the network genuinely can't cover the request.
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
        // Splitting means two parcels and two chances for a leg to go missing - a fallback, never a habit.
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
        // Milestone 73: the reservation that crosses the reorder point is
        // news; the next twenty orders finding it already low are not.
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
