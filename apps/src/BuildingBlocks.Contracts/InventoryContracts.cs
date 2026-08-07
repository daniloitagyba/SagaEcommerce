namespace BuildingBlocks;

/// <summary>
/// Milestone 41: reservation command/reply contracts for Inventory.Service.
/// Plain JSON, not schema-registered - internal request/reply, not a domain
/// event other consumers evolve against. Must be produced keyed by Sku, not
/// OrderId: Kafka's per-partition ownership then makes every request for a
/// given SKU handled strictly one-at-a-time, which is the only thing
/// preventing an oversell race - Inventory.Service takes no database row
/// lock to enforce it separately.
/// </summary>
public sealed record InventoryReservationRequested(
    Guid ReservationId,
    Guid OrderId,
    string Sku,
    int Quantity,
    string CorrelationId,
    DateTimeOffset RequestedAt);

public sealed record InventoryReservationReplied(
    Guid ReservationId,
    Guid OrderId,
    string Sku,
    int Quantity,
    bool Reserved,
    string? Reason,
    string CorrelationId,
    DateTimeOffset DecidedAt,
    // Milestone 74: distinguishes "wait for a restock" from "give up" -
    // both are Reserved: false, but only the first stops the saga from
    // cancelling. A stale message without this field deserializes it
    // false, which is the pre-existing cancel-outright behaviour.
    bool Backordered = false);

/// <summary>
/// Milestone 43: how a reservation is settled - Commit makes the hold
/// permanent (payment approved), Release gives it back (payment declined,
/// the saga's compensating transaction). Both reuse the original
/// ReservationId, not a new one, so the lifecycle correlates in logs, and
/// are keyed by Sku for the same partition-ownership reason as the request.
/// </summary>
public sealed record InventoryReservationCommitRequested(
    Guid ReservationId,
    Guid OrderId,
    string Sku,
    int Quantity,
    string CorrelationId,
    DateTimeOffset RequestedAt);

public sealed record InventoryReservationCommitReplied(
    Guid ReservationId,
    Guid OrderId,
    string Sku,
    int Quantity,
    bool Committed,
    string? Reason,
    string CorrelationId,
    DateTimeOffset DecidedAt);

public sealed record InventoryReservationReleaseRequested(
    Guid ReservationId,
    Guid OrderId,
    string Sku,
    int Quantity,
    string CorrelationId,
    DateTimeOffset RequestedAt);

public sealed record InventoryReservationReleaseReplied(
    Guid ReservationId,
    Guid OrderId,
    string Sku,
    int Quantity,
    bool Released,
    string? Reason,
    string CorrelationId,
    DateTimeOffset DecidedAt);

/// <summary>
/// Milestone 73: a warehouse has fallen to or below its reorder point - the
/// signal a replenishment process would consume, though nothing in this lab
/// consumes it yet; the event is emitted durably, and the consumer is
/// somebody else's milestone. Emitted on the <em>crossing</em>, not on
/// every reservation that finds a warehouse already low, or a brisk-selling
/// depleted warehouse would publish one per order.
/// </summary>
public sealed record WarehouseReplenishmentNeeded(
    string Sku,
    string WarehouseCode,
    int AvailableQuantity,
    int ReorderPoint,
    string CorrelationId,
    DateTimeOffset DetectedAt);
