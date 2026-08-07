namespace BuildingBlocks;

/// <summary>
/// Milestone 70: Orders tells Payments to give money back for a return.
///
/// Carries the amount rather than letting Payments recompute it: the refund
/// is a slice of what a specific line was <em>charged</em>, net of the
/// discount it received, and Payments has no idea an order even has lines.
/// The number is computed once, at the point that owns it, and travels.
/// </summary>
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
    DateTimeOffset RequestedAt);

public sealed record InventoryRestockReplied(
    Guid ReturnId,
    Guid OrderId,
    string Sku,
    int Quantity,
    bool Restocked,
    string CorrelationId,
    DateTimeOffset DecidedAt);
