namespace Inventory.Service.Domain;

/// <summary>
/// The permanent ledger a prior audit
/// wanted and couldn't build without a live database to validate a
/// migration against - "a permanent ledger of 'this order drew down this
/// much stock, from this warehouse, and here is its current status' that
/// survives settlement." <see cref="ReservationAllocation"/> deliberately
/// does not survive settlement (it exists only long enough for commit or
/// release to replay it, then <c>WarehouseAllocationStore.TrySettleReservationAsync</c>
/// deletes it) - this is the row that does, written once, at the moment a
/// reservation actually commits (draws stock down for real), and reduced
/// or removed only when a restock referencing the same order and sku gives
/// some or all of it back. What's left with <see cref="Quantity"/> still
/// positive, for an order whose status has since become Cancelled, is
/// exactly the leak that audit found.
/// </summary>
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
