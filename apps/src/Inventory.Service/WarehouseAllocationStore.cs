using Inventory.Service.Data;
using Inventory.Service.Domain;
using Microsoft.EntityFrameworkCore;

namespace Inventory.Service;

/// <summary>
/// Milestone 72: applies an allocation plan to the warehouse network.
///
/// Reserve draws the plan and records it; commit and release replay exactly
/// what reserve recorded, rather than guessing which building the stock
/// came from. Guessing wrong moves stock between warehouses on paper, which
/// is the kind of discrepancy nobody notices until a stocktake.
///
/// <para>
/// No row locking, and none needed: Inventory.Service consumes reservation
/// commands partitioned by SKU (Milestone 41), so two requests for the same
/// SKU are never processed concurrently in the first place. That guarantee
/// is what lets the allocator read availability, decide, and write without
/// the read going stale in between - the same reasoning
/// <see cref="InventoryItem"/> has relied on since it was written, now
/// spanning several rows instead of one.
/// </para>
/// </summary>
public sealed class WarehouseAllocationStore(InventoryDbContext dbContext)
{
    /// <summary>Which warehouses can supply this SKU, in the allocator's terms.</summary>
    public async Task<IReadOnlyList<StockAllocator.Candidate>> GetCandidatesAsync(
        string sku,
        CancellationToken cancellationToken)
    {
        var stocks = await dbContext.WarehouseStocks
            .Where(stock => stock.Sku == sku)
            .ToListAsync(cancellationToken);

        return
        [
            .. stocks.Select(stock => new StockAllocator.Candidate(
                stock.WarehouseCode,
                stock.AvailableQuantity,
                // Priority is the warehouse code's ordinal position for now -
                // a real network would rank by distance to the destination,
                // which needs the shipping address to reach this service.
                WarehousePriority(stock.WarehouseCode)))
        ];
    }

    /// <summary>
    /// The outcome of applying a plan: whether it went through, and which
    /// warehouses fell to or below their reorder point as a result.
    /// </summary>
    public sealed record ReservationOutcome(bool Applied, IReadOnlyList<WarehouseStock> CrossedReorderPoint)
    {
        public static ReservationOutcome Refused => new(false, []);
    }

    /// <summary>
    /// Applies a plan and records it. Returns not-applied when any leg
    /// fails, which can only happen if availability moved between the read
    /// and the write - impossible under per-SKU partitioning, but checked
    /// rather than assumed, because the assumption is the load-bearing one.
    /// </summary>
    public async Task<ReservationOutcome> TryApplyReservationAsync(
        Guid reservationId,
        string sku,
        StockAllocator.Plan plan,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        if (!plan.Fulfillable)
        {
            return ReservationOutcome.Refused;
        }

        var crossed = new List<WarehouseStock>();

        foreach (var line in plan.Lines)
        {
            var stock = await dbContext.WarehouseStocks
                .SingleOrDefaultAsync(item => item.Sku == sku && item.WarehouseCode == line.WarehouseCode, cancellationToken);

            if (stock is null)
            {
                return ReservationOutcome.Refused;
            }

            // Sampled before the reservation, so what is reported is the
            // moment the warehouse went low - not every subsequent order
            // that finds it already low.
            var wasStocked = !stock.NeedsReplenishment;

            if (!stock.TryReserve(line.Quantity, now))
            {
                return ReservationOutcome.Refused;
            }

            if (wasStocked && stock.NeedsReplenishment)
            {
                crossed.Add(stock);
            }

            dbContext.ReservationAllocations.Add(
                ReservationAllocation.Create(reservationId, sku, line.WarehouseCode, line.Quantity, now));
        }

        return new ReservationOutcome(true, crossed);
    }

    /// <summary>
    /// The outcome of a full network-plus-aggregate reservation decision.
    /// </summary>
    public sealed record ReservationDecision(bool Reserved, IReadOnlyList<WarehouseStock> CrossedReorderPoint)
    {
        public static ReservationDecision Refused => new(false, []);
    }

    /// <summary>
    /// Milestone 74: the whole decide-then-mutate reservation - network and
    /// aggregate together - factored out from
    /// InventoryReservationMessageProcessor.ProcessAsync so the backorder
    /// release path can retry the exact same decision after a restock
    /// rather than re-deriving it. Two call sites separately chaining
    /// "check, then mutate" is exactly how Milestone 72's bug happened the
    /// first time: one of them drifts, and a warehouse ends up holding a
    /// reservation for an order that was told no.
    /// </summary>
    public async Task<ReservationDecision> TryReserveAsync(
        Guid reservationId,
        string sku,
        int quantity,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var candidates = await GetCandidatesAsync(sku, cancellationToken);
        var plan = StockAllocator.Allocate(candidates, quantity);

        var item = await dbContext.InventoryItems.FirstOrDefaultAsync(entity => entity.Sku == sku, cancellationToken);

        if (!plan.Fulfillable || item is null || item.AvailableQuantity < quantity)
        {
            return ReservationDecision.Refused;
        }

        var outcome = await TryApplyReservationAsync(reservationId, sku, plan, now, cancellationToken);
        if (!outcome.Applied || !item.TryReserve(quantity, now))
        {
            return ReservationDecision.Refused;
        }

        return new ReservationDecision(true, outcome.CrossedReorderPoint);
    }

    /// <summary>Replays a recorded allocation as a commit or a release.</summary>
    public async Task<bool> TrySettleReservationAsync(
        Guid reservationId,
        bool commit,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var allocations = await dbContext.ReservationAllocations
            .Where(allocation => allocation.ReservationId == reservationId)
            .ToListAsync(cancellationToken);

        if (allocations.Count == 0)
        {
            // Nothing recorded - either an unknown reservation or one made
            // before this milestone. The caller falls back to the
            // single-warehouse path so orders in flight during the rollout
            // still settle.
            return false;
        }

        foreach (var allocation in allocations)
        {
            var stock = await dbContext.WarehouseStocks
                .SingleOrDefaultAsync(
                    item => item.Sku == allocation.Sku && item.WarehouseCode == allocation.WarehouseCode,
                    cancellationToken);

            if (stock is null)
            {
                return false;
            }

            var applied = commit
                ? stock.TryCommit(allocation.Quantity, now)
                : stock.TryRelease(allocation.Quantity, now);

            if (!applied)
            {
                return false;
            }
        }

        dbContext.ReservationAllocations.RemoveRange(allocations);
        return true;
    }

    /// <summary>
    /// Returned units go back to the warehouse with the most room, which in
    /// this lab is a stand-in for "wherever the returns depot routes them" -
    /// a real network decides from the return label, not the stock levels.
    /// </summary>
    public async Task<bool> TryRestockAsync(
        string sku,
        int quantity,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var stock = await dbContext.WarehouseStocks
            .Where(item => item.Sku == sku)
            .OrderBy(item => item.AvailableQuantity)
            .FirstOrDefaultAsync(cancellationToken);

        if (stock is null)
        {
            return false;
        }

        stock.Restock(quantity, now);
        return true;
    }

    private static int WarehousePriority(string warehouseCode) => warehouseCode switch
    {
        "WH-SP" => 1,
        "WH-RJ" => 2,
        _ => 9
    };
}
