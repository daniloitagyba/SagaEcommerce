namespace Inventory.Service.Domain;

/// <summary>Stock of one SKU in one warehouse; one row per (SKU, warehouse).</summary>
public sealed class WarehouseStock
{
    private WarehouseStock()
    {
    }

    public string Sku { get; private set; } = string.Empty;

    public string WarehouseCode { get; private set; } = string.Empty;

    public int AvailableQuantity { get; private set; }

    public int ReservedQuantity { get; private set; }

    /// <summary>Threshold below which the warehouse should be replenished.</summary>
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

/// <summary>Records which warehouses a reservation drew from, so commit and release can undo precisely what reserve did.</summary>
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

/// <summary>Decides which warehouses fulfil a reservation, preferring the fewest warehouses possible.</summary>
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
            return Plan.Unfulfillable;
        }

        var single = ordered.FirstOrDefault(candidate => candidate.Available >= requestedQuantity);
        if (single is not null)
        {
            return new Plan(true, [new StockAllocationLine(single.WarehouseCode, requestedQuantity)]);
        }

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
