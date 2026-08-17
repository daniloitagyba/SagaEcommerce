namespace Inventory.Service.Domain;

/// <summary>An order waiting for stock that was not available when it was requested.</summary>
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
