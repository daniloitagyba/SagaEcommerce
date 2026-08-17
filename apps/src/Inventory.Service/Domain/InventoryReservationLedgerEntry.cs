namespace Inventory.Service.Domain;

/// <summary>Permanent ledger of stock a reservation committed, reduced or removed only as restocks give it back.</summary>
public sealed class InventoryReservationLedgerEntry
{
    private InventoryReservationLedgerEntry()
    {
    }

    public Guid Id { get; private set; }

    public Guid ReservationId { get; private set; }

    public Guid OrderId { get; private set; }

    public string Sku { get; private set; } = string.Empty;

    public int Quantity { get; private set; }

    public DateTimeOffset CommittedAt { get; private set; }

    public static InventoryReservationLedgerEntry Create(Guid reservationId, Guid orderId, string sku, int quantity, DateTimeOffset now) =>
        new()
        {
            Id = Guid.NewGuid(),
            ReservationId = reservationId,
            OrderId = orderId,
            Sku = sku,
            Quantity = quantity,
            CommittedAt = now
        };

    /// <summary>A restock giving some (or all) of this entry's committed quantity back.</summary>
    public void Reduce(int quantity) => Quantity -= quantity;
}
