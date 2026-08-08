namespace Inventory.Service.Domain;

/// <summary>
/// Closes a loop opened earlier and explicitly left
/// open - <c>WarehouseReplenishmentNeeded</c> has been emitted, durably,
/// since then, and nothing consumed it. A purchase order is this
/// lab's stand-in for "a human or a supplier's system now knows to send
/// more stock" - requested the moment the signal arrives, received after a
/// lab-scale compression of a real supplier's lead time (the same
/// "minutes instead of days" trade already made for a
/// boleto's due date), at which point the stock it represents actually
/// lands and restocks the warehouse it was requested for.
/// </summary>
public sealed class PurchaseOrder
{
    private PurchaseOrder()
    {
    }

    public Guid Id { get; private set; }

    public string Sku { get; private set; } = string.Empty;

    public string WarehouseCode { get; private set; } = string.Empty;

    public int Quantity { get; private set; }

    public string State { get; private set; } = PurchaseOrderStates.Requested;

    public string CorrelationId { get; private set; } = string.Empty;

    public DateTimeOffset RequestedAt { get; private set; }

    public DateTimeOffset? ReceivedAt { get; private set; }

    public static PurchaseOrder Create(string sku, string warehouseCode, int quantity, string correlationId, DateTimeOffset now) =>
        new()
        {
            Id = Guid.NewGuid(),
            Sku = sku,
            WarehouseCode = warehouseCode,
            Quantity = quantity,
            State = PurchaseOrderStates.Requested,
            CorrelationId = correlationId,
            RequestedAt = now
        };

    /// <summary>False if already received - the receiving sweep's claim query already excludes these, but the guard makes a redelivered claim a no-op instead of a second restock.</summary>
    public bool TryReceive(DateTimeOffset now)
    {
        if (State != PurchaseOrderStates.Requested)
        {
            return false;
        }

        State = PurchaseOrderStates.Received;
        ReceivedAt = now;
        return true;
    }
}

public static class PurchaseOrderStates
{
    public const string Requested = "Requested";
    public const string Received = "Received";
}
