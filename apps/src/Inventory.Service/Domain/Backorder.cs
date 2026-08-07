namespace Inventory.Service.Domain;

/// <summary>
/// Milestone 74: an order waiting for stock that was not there when it
/// asked.
///
/// Keyed by <see cref="ReservationId"/> - the same id the saga already
/// tracks - rather than a surrogate key, because the release path never
/// needs to look one up any other way, and reusing it is what lets the
/// eventual success reply carry the exact id the saga is still waiting on
/// (see InventoryReservationMessageProcessor.TryFulfillBackordersAsync).
/// </summary>
public sealed class Backorder
{
    private Backorder()
    {
    }

    public Guid ReservationId { get; private set; }

    public Guid OrderId { get; private set; }

    public string Sku { get; private set; } = string.Empty;

    public int Quantity { get; private set; }

    public string CorrelationId { get; private set; } = string.Empty;

    public DateTimeOffset RequestedAt { get; private set; }

    public static Backorder Create(
        Guid reservationId, Guid orderId, string sku, int quantity, string correlationId, DateTimeOffset now) =>
        new()
        {
            ReservationId = reservationId,
            OrderId = orderId,
            Sku = sku,
            Quantity = quantity,
            CorrelationId = correlationId,
            RequestedAt = now
        };
}
