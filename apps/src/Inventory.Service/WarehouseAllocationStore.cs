using Inventory.Service.Data;
using Inventory.Service.Domain;
using Microsoft.EntityFrameworkCore;

namespace Inventory.Service;

/// <summary>
/// Applies an allocation plan to the warehouse network. Reserve draws the
/// plan and records it; commit and release replay exactly what reserve
/// recorded rather than guessing which building the stock came from.
///
/// Mutating callers hold a transaction-scoped advisory lock for the SKU.
/// That lock is deliberately acquired by the message processor rather than
/// inferred from Kafka partitioning: stock changes arrive on several topics,
/// whose partitions can be assigned to different replicas concurrently.
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
    /// fails. The caller's SKU lock normally keeps availability stable, but
    /// the full validation remains a defensive invariant for direct callers
    /// and corrupted allocation plans.
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

        var warehouseCodes = plan.Lines.Select(line => line.WarehouseCode).ToArray();
        var stocks = await dbContext.WarehouseStocks
            .Where(item => item.Sku == sku && warehouseCodes.Contains(item.WarehouseCode))
            .ToDictionaryAsync(item => item.WarehouseCode, StringComparer.Ordinal, cancellationToken);

        // Validate every leg before mutating any tracked entity. Returning
        // after the first mutation used to leave a partial plan pending in
        // the DbContext, which the caller then persisted with the reply.
        if (stocks.Count != plan.Lines.Count
            || plan.Lines.Any(line => !stocks.TryGetValue(line.WarehouseCode, out var stock)
                || line.Quantity <= 0
                || stock.AvailableQuantity < line.Quantity))
        {
            return ReservationOutcome.Refused;
        }

        var crossed = new List<WarehouseStock>();

        foreach (var line in plan.Lines)
        {
            var stock = stocks[line.WarehouseCode];

            // Sampled before the reservation, so what is reported is the
            // moment the warehouse went low - not every subsequent order
            // that finds it already low.
            var wasStocked = !stock.NeedsReplenishment;

            _ = stock.TryReserve(line.Quantity, now);

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
    /// The whole decide-then-mutate reservation - network and aggregate
    /// together - shared by the normal reserve path and backorder release
    /// so neither can drift from the other's decision.
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
        if (!outcome.Applied)
        {
            return ReservationDecision.Refused;
        }

        await SynchronizeItemAsync(item, sku, now, cancellationToken);

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
            // Nothing recorded means an unknown or already-settled
            // reservation. Guessing a warehouse here could settle stock
            // owned by a different order, so failure is explicit.
            return false;
        }

        var sku = allocations[0].Sku;
        if (allocations.Any(allocation => !string.Equals(allocation.Sku, sku, StringComparison.Ordinal)))
        {
            return false;
        }

        var warehouseCodes = allocations.Select(allocation => allocation.WarehouseCode).Distinct(StringComparer.Ordinal).ToArray();
        var stocks = await dbContext.WarehouseStocks
            .Where(item => item.Sku == sku && warehouseCodes.Contains(item.WarehouseCode))
            .ToDictionaryAsync(item => item.WarehouseCode, StringComparer.Ordinal, cancellationToken);
        var item = await dbContext.InventoryItems.SingleOrDefaultAsync(entity => entity.Sku == sku, cancellationToken);

        // As with reserve, validate the entire recorded plan before
        // applying it. A missing or insufficient later leg must not leave
        // earlier warehouse rows committed or released.
        if (item is null
            || stocks.Count != warehouseCodes.Length
            || allocations.Any(allocation => !stocks.TryGetValue(allocation.WarehouseCode, out var stock)
                || allocation.Quantity <= 0
                || stock.ReservedQuantity < allocation.Quantity))
        {
            return false;
        }

        foreach (var allocation in allocations)
        {
            var stock = stocks[allocation.WarehouseCode];
            _ = commit
                ? stock.TryCommit(allocation.Quantity, now)
                : stock.TryRelease(allocation.Quantity, now);
        }

        dbContext.ReservationAllocations.RemoveRange(allocations);
        await SynchronizeItemAsync(item, sku, now, cancellationToken);
        return true;
    }

    /// <summary>
    /// The permanent half of settlement -
    /// ReservationAllocation is deleted the moment TrySettleReservationAsync
    /// replays it (by design, so it doesn't linger); this is what survives,
    /// specifically so a later anti-entropy sweep can still ask whether the
    /// order this stock was drawn down for is still one that should have it.
    /// Called only on a successful commit, never a release - a released
    /// reservation never drew anything down permanently, so there is
    /// nothing here for it to leak.
    /// </summary>
    public Task RecordCommittedAsync(
        Guid reservationId, Guid orderId, string sku, int quantity, DateTimeOffset now, CancellationToken cancellationToken)
    {
        dbContext.ReservationLedgerEntries.Add(
            InventoryReservationLedgerEntry.Create(reservationId, orderId, sku, quantity, now));
        return Task.CompletedTask;
    }

    /// <summary>
    /// The other half: a restock (a return, an operator cancellation, a
    /// backorder cancellation - anything that gives committed stock back)
    /// reduces the oldest still-open ledger entries for the same order and
    /// sku first, deleting one once it reaches zero. Quantity, not a
    /// specific warehouse or reservation, is what's reconciled here - the
    /// ledger tracks what a customer order still owes, and a return's
    /// restock may land in a different warehouse than commit drew from even
    /// though TryRestockAsync now honours a warehouse code when one is
    /// given - and the anti-entropy check this feeds only ever asks "does
    /// this order still have committed stock outstanding," not "which shelf."
    /// A restock whose order id was never a real customer order (the
    /// replenishment loop's restocks carry a purchase order's own id) simply
    /// matches nothing here and is a harmless no-op.
    /// </summary>
    public async Task ResolveLedgerOnRestockAsync(
        Guid orderId, string sku, int quantity, CancellationToken cancellationToken)
    {
        var remaining = quantity;
        var entries = await dbContext.ReservationLedgerEntries
            .Where(entry => entry.OrderId == orderId && entry.Sku == sku)
            .OrderBy(entry => entry.CommittedAt)
            .ToListAsync(cancellationToken);

        foreach (var entry in entries)
        {
            if (remaining <= 0)
            {
                break;
            }

            var reduceBy = Math.Min(entry.Quantity, remaining);
            entry.Reduce(reduceBy);
            remaining -= reduceBy;

            if (entry.Quantity <= 0)
            {
                dbContext.ReservationLedgerEntries.Remove(entry);
            }
        }
    }

    /// <summary>
    /// Restocks the warehouse the caller names, when it names one - the
    /// replenishment loop always does, since its purchase order was raised
    /// against the exact warehouse that crossed its reorder point, and
    /// landing the stock anywhere else leaves that warehouse silently
    /// under-stocked (the reorder signal is edge-triggered, so it does not
    /// fire again). When no warehouse is named - a customer return, where
    /// nothing upstream knows which depot the parcel will land at - falls
    /// back to the warehouse with the most room, which in this lab is a
    /// stand-in for "wherever the returns depot routes them"; a real
    /// network would decide from the return label, not the stock levels.
    /// </summary>
    public async Task<bool> TryRestockAsync(
        string sku,
        int quantity,
        DateTimeOffset now,
        string? warehouseCode,
        CancellationToken cancellationToken)
    {
        var stocks = await dbContext.WarehouseStocks
            .Where(item => item.Sku == sku)
            .OrderByDescending(item => item.AvailableQuantity)
            .ToListAsync(cancellationToken);

        var item = await dbContext.InventoryItems.SingleOrDefaultAsync(entity => entity.Sku == sku, cancellationToken);
        if (item is null || quantity <= 0)
        {
            return false;
        }

        var stock = string.IsNullOrEmpty(warehouseCode)
            ? stocks.Count > 0 ? stocks[0] : null
            : stocks.SingleOrDefault(candidate => candidate.WarehouseCode == warehouseCode);
        if (stock is null)
        {
            return false;
        }

        stock.Restock(quantity, now);
        item.SynchronizeFromWarehouses(
            stocks.Sum(candidate => candidate.AvailableQuantity),
            stocks.Sum(candidate => candidate.ReservedQuantity),
            now);
        return true;
    }

    private async Task SynchronizeItemAsync(
        InventoryItem item,
        string sku,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        await dbContext.WarehouseStocks
            .Where(stock => stock.Sku == sku)
            .LoadAsync(cancellationToken);

        var stocks = dbContext.WarehouseStocks.Local
            .Where(stock => string.Equals(stock.Sku, sku, StringComparison.Ordinal))
            .ToList();

        item.SynchronizeFromWarehouses(
            stocks.Sum(stock => stock.AvailableQuantity),
            stocks.Sum(stock => stock.ReservedQuantity),
            now);
    }

    private static int WarehousePriority(string warehouseCode) => warehouseCode switch
    {
        "WH-SP" => 1,
        "WH-RJ" => 2,
        _ => 9
    };
}
