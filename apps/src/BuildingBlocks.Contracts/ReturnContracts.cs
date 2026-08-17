namespace BuildingBlocks;

/// <summary>Orders tells Payments to give money back for a return.</summary>
public sealed record PaymentRefundRequested(
    Guid OrderId,
    Guid ReturnId,
    decimal Amount,
    string Currency,
    string Reason,
    string CorrelationId,
    DateTimeOffset RequestedAt);

/// <summary>Orders tells Inventory that returned units are sellable again.</summary>
public sealed record InventoryRestockRequested(
    Guid ReturnId,
    Guid OrderId,
    string Sku,
    int Quantity,
    string CorrelationId,
    DateTimeOffset RequestedAt,
    string? WarehouseCode = null);

public sealed record InventoryRestockReplied(
    Guid ReturnId,
    Guid OrderId,
    string Sku,
    int Quantity,
    bool Restocked,
    string CorrelationId,
    DateTimeOffset DecidedAt,
    string? WarehouseCode = null);
