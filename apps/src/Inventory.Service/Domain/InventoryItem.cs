namespace Inventory.Service.Domain;

/// <summary>
/// Deliberately no optimistic concurrency token and no row lock - TryReserve
/// is a plain read-then-write, safe only because the Kafka consumer
/// guarantees at most one in-flight request per Sku (InventoryContracts.cs).
/// </summary>
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
        if (AvailableQuantity < quantity)
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
        if (ReservedQuantity < quantity)
        {
            return false;
        }

        ReservedQuantity -= quantity;
        UpdatedAt = now;
        return true;
    }

    /// <summary>
    /// Milestone 70: puts returned units back on the shelf. Distinct from
    /// TryRelease, which hands back stock only ever <em>held</em> - a
    /// return means the sale happened and stock already left inventory, so
    /// there is no ReservedQuantity to draw down. A pure increment; cannot fail.
    /// </summary>
    public void Restock(int quantity, DateTimeOffset now)
    {
        if (quantity <= 0)
        {
            return;
        }

        AvailableQuantity += quantity;
        UpdatedAt = now;
    }

    /// <summary>The saga's compensating transaction: gives held stock back when a downstream step (payment) fails.</summary>
    public bool TryRelease(int quantity, DateTimeOffset now)
    {
        if (ReservedQuantity < quantity)
        {
            return false;
        }

        ReservedQuantity -= quantity;
        AvailableQuantity += quantity;
        UpdatedAt = now;
        return true;
    }
}
