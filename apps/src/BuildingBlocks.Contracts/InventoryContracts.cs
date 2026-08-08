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
/// Milestone 73: a warehouse has fallen to or below its reorder point.
/// Emitted on the <em>crossing</em>, not on every reservation that finds a
/// warehouse already low, or a brisk-selling depleted warehouse would
/// publish one per order.
///
/// Milestone 89: EventId is what finally consumes this signal -
/// ReplenishmentRequestProcessor's inbox dedup needs a stable identifier,
/// which this event never carried while nothing consumed it. Minted once,
/// at publish time (EnqueueReplenishmentSignals), so a redelivery carries
/// the same id rather than minting a new one that would look like a second, distinct crossing.
/// </summary>
public sealed record WarehouseReplenishmentNeeded(
    Guid EventId,
    string Sku,
    string WarehouseCode,
    int AvailableQuantity,
    int ReorderPoint,
    string CorrelationId,
    DateTimeOffset DetectedAt);

/// <summary>
/// Milestone 81: an order sitting in <see cref="OrderStatuses.Backordered"/>
/// was cancelled - stop waiting for stock on its behalf. Carries only
/// OrderId, not a per-line reservation id: a backordered order can have
/// several waiting lines (Milestone 78), and the FIFO release path
/// (<c>ReleaseBackordersAsync</c>) indexes backorders by Sku, not by any id
/// Orders holds, so there is nothing more specific to reuse. Inventory
/// deletes every backorder row for this OrderId in one statement - plain
/// idempotent by construction, since deleting rows that are already gone is
/// a no-op.
/// </summary>
public sealed record BackorderCancellationRequested(
    Guid OrderId,
    string CorrelationId,
    DateTimeOffset RequestedAt);
