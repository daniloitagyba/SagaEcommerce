namespace Inventory.Service.Domain;

/// <summary>
/// Stock of one SKU in one warehouse. Milestone 72 splits what used to be a
/// single row per SKU into one row per (SKU, warehouse), because "do we
/// have 3 of these?" and "can we ship 3 of these from somewhere" stopped
/// being the same question the moment there was more than one somewhere.
/// </summary>
public sealed class WarehouseStock
{
    private WarehouseStock()
    {
    }

    public string Sku { get; private set; } = string.Empty;

    public string WarehouseCode { get; private set; } = string.Empty;

    public int AvailableQuantity { get; private set; }

    public int ReservedQuantity { get; private set; }

    /// <summary>
    /// Below this, the warehouse should be replenished. Reporting only -
    /// nothing in this lab acts on it, but it is the number a replenishment
    /// job would read, and leaving it out would make the model quietly
    /// claim stock levels need no management.
    /// </summary>
    public int ReorderPoint { get; private set; }

    public DateTimeOffset UpdatedAt { get; private set; }

    public bool NeedsReplenishment => AvailableQuantity <= ReorderPoint;

    public static WarehouseStock Create(string sku, string warehouseCode, int available, int reorderPoint, DateTimeOffset now) =>
        new()
        {
            Sku = sku,
            WarehouseCode = warehouseCode,
            AvailableQuantity = available,
            ReservedQuantity = 0,
            ReorderPoint = reorderPoint,
            UpdatedAt = now
        };

    public bool TryReserve(int quantity, DateTimeOffset now)
    {
        if (quantity <= 0 || AvailableQuantity < quantity)
        {
            return false;
        }

        AvailableQuantity -= quantity;
        ReservedQuantity += quantity;
        UpdatedAt = now;
        return true;
    }

    public bool TryCommit(int quantity, DateTimeOffset now)
    {
        if (quantity <= 0 || ReservedQuantity < quantity)
        {
            return false;
        }

        ReservedQuantity -= quantity;
        UpdatedAt = now;
        return true;
    }

    public bool TryRelease(int quantity, DateTimeOffset now)
    {
        if (quantity <= 0 || ReservedQuantity < quantity)
        {
            return false;
        }

        ReservedQuantity -= quantity;
        AvailableQuantity += quantity;
        UpdatedAt = now;
        return true;
    }

    public void Restock(int quantity, DateTimeOffset now)
    {
        if (quantity <= 0)
        {
            return;
        }

        AvailableQuantity += quantity;
        UpdatedAt = now;
    }
}

/// <summary>One warehouse's share of a single reservation.</summary>
public sealed record StockAllocationLine(string WarehouseCode, int Quantity);

/// <summary>
/// Which warehouses a reservation actually drew from, recorded so commit
/// and release can undo precisely what reserve did.
///
/// Without this, a reservation spanning two warehouses could only be
/// released by guessing - and guessing wrong moves stock between buildings
/// on paper, which is the kind of discrepancy nobody notices until a
/// stocktake.
/// </summary>
public sealed class ReservationAllocation
{
    private ReservationAllocation()
    {
    }

    public Guid Id { get; private set; }

    public Guid ReservationId { get; private set; }

    public string Sku { get; private set; } = string.Empty;

    public string WarehouseCode { get; private set; } = string.Empty;

    public int Quantity { get; private set; }

    public DateTimeOffset AllocatedAt { get; private set; }

    public static ReservationAllocation Create(Guid reservationId, string sku, string warehouseCode, int quantity, DateTimeOffset now) =>
        new()
        {
            Id = Guid.NewGuid(),
            ReservationId = reservationId,
            Sku = sku,
            WarehouseCode = warehouseCode,
            Quantity = quantity,
            AllocatedAt = now
        };
}

/// <summary>
/// Milestone 72: decides which warehouses fulfil a reservation.
///
/// Pure, and deliberately so - the interesting part is the allocation
/// policy, and a policy that can only be exercised against a live database
/// is a policy nobody property-tests. Given the same availability it always
/// produces the same plan, which also means two replicas reasoning about
/// the same stock reach the same answer.
///
/// <para>
/// The policy is <b>fewest warehouses first</b>: prefer a single warehouse
/// that can cover the whole order, and only split when none can. Splitting
/// is not free - it means two parcels, two shipping costs and two chances
/// for one leg to go missing - so it is a fallback rather than the default.
/// Ties break on the warehouse's configured priority and then its code, so
/// the result never depends on dictionary ordering.
/// </para>
/// </summary>
public static class StockAllocator
{
    public sealed record Candidate(string WarehouseCode, int Available, int Priority);

    public sealed record Plan(bool Fulfillable, IReadOnlyList<StockAllocationLine> Lines)
    {
        public static Plan Unfulfillable => new(false, []);
    }

    public static Plan Allocate(IReadOnlyList<Candidate> candidates, int requestedQuantity)
    {
        if (requestedQuantity <= 0 || candidates.Count == 0)
        {
            return Plan.Unfulfillable;
        }

        var ordered = candidates
            .Where(candidate => candidate.Available > 0)
            .OrderBy(candidate => candidate.Priority)
            .ThenBy(candidate => candidate.WarehouseCode, StringComparer.Ordinal)
            .ToList();

        if (ordered.Sum(candidate => (long)candidate.Available) < requestedQuantity)
        {
            // Not enough anywhere. Reporting this rather than allocating
            // what exists is deliberate: the saga's reservation step is
            // all-or-nothing, and a partial reservation would confirm an
            // order the warehouse cannot actually fill.
            return Plan.Unfulfillable;
        }

        // Single warehouse if one can cover it - highest priority among
        // those that can, not merely the largest.
        var single = ordered.FirstOrDefault(candidate => candidate.Available >= requestedQuantity);
        if (single is not null)
        {
            return new Plan(true, [new StockAllocationLine(single.WarehouseCode, requestedQuantity)]);
        }

        // Otherwise draw down in priority order until satisfied.
        var lines = new List<StockAllocationLine>();
        var remaining = requestedQuantity;

        foreach (var candidate in ordered)
        {
            if (remaining == 0)
            {
                break;
            }

            var take = Math.Min(candidate.Available, remaining);
            lines.Add(new StockAllocationLine(candidate.WarehouseCode, take));
            remaining -= take;
        }

        return new Plan(true, lines);
    }
}
