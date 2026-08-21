namespace Inventory.Service.Domain;

/// <summary>Per-SKU inventory read model with reserve/commit/release/restock operations.</summary>
public sealed class InventoryItem
{
    private InventoryItem()
    {
    }

    public string Sku { get; private set; } = string.Empty;

    public int AvailableQuantity { get; private set; }

    public int ReservedQuantity { get; private set; }

    public DateTimeOffset UpdatedAt { get; private set; }

    public static InventoryItem Create(string sku, int availableQuantity, DateTimeOffset now)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sku);
        ArgumentOutOfRangeException.ThrowIfNegative(availableQuantity);

        return new InventoryItem
        {
            Sku = sku,
            AvailableQuantity = availableQuantity,
            ReservedQuantity = 0,
            UpdatedAt = now
        };
    }

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

    /// <summary>Turns a temporary hold into a permanent deduction - the saga's "everything downstream succeeded" outcome.</summary>
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

    public void Restock(int quantity, DateTimeOffset now)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(quantity);

        AvailableQuantity += quantity;
        UpdatedAt = now;
    }

    /// <summary>The saga's compensating transaction: gives held stock back when a downstream step (payment) fails.</summary>
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

    public void SynchronizeFromWarehouses(int availableQuantity, int reservedQuantity, DateTimeOffset now)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(availableQuantity);
        ArgumentOutOfRangeException.ThrowIfNegative(reservedQuantity);

        AvailableQuantity = availableQuantity;
        ReservedQuantity = reservedQuantity;
        UpdatedAt = now;
    }
}
