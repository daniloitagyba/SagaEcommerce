using Inventory.Service.Data;
using Inventory.Service.Domain;
using Microsoft.EntityFrameworkCore;

namespace Inventory.Service;

/// <summary>Applies an allocation plan to the warehouse network.</summary>
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
                WarehousePriority(stock.WarehouseCode)))
        ];
    }

    /// <summary>Outcome of applying a plan: whether it went through, and which warehouses fell to or below their reorder point.</summary>
    public sealed record ReservationOutcome(bool Applied, IReadOnlyList<WarehouseStock> CrossedReorderPoint)
    {
        public static ReservationOutcome Refused => new(false, []);
    }

    /// <summary>Applies a plan and records it; returns not-applied when any leg fails.</summary>
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

    /// <summary>Outcome of a full network-plus-aggregate reservation decision.</summary>
    public sealed record ReservationDecision(bool Reserved, IReadOnlyList<WarehouseStock> CrossedReorderPoint)
    {
        public static ReservationDecision Refused => new(false, []);
    }

    /// <summary>The whole decide-then-mutate reservation, shared by the normal reserve path and backorder release.</summary>
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

    /// <summary>Records the permanent half of settlement, called only on a successful commit, never a release.</summary>
    public Task RecordCommittedAsync(
        Guid reservationId, Guid orderId, string sku, int quantity, DateTimeOffset now, CancellationToken cancellationToken)
    {
        dbContext.ReservationLedgerEntries.Add(
            InventoryReservationLedgerEntry.Create(reservationId, orderId, sku, quantity, now));
        return Task.CompletedTask;
    }

    /// <summary>Reduces the oldest still-open ledger entries for an order/sku as stock is given back, deleting an entry once it reaches zero.</summary>
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

    /// <summary>Restocks the warehouse the caller names; falls back to the warehouse with the most room when none is named.</summary>
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
