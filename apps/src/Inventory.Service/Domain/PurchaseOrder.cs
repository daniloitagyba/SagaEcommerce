namespace Inventory.Service.Domain;

/// <summary>A request to a supplier to restock a warehouse, requested on signal and received once stock lands.</summary>
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
