namespace BuildingBlocks;

/// <summary>Reservation command/reply contracts for Inventory.Service.</summary>
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
    bool Backordered = false);

/// <summary>How a reservation is settled: Commit makes the hold permanent, Release gives it back.</summary>
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

/// <summary>A warehouse has fallen to or below its reorder point; emitted on the crossing, not on every low reservation.</summary>
public sealed record WarehouseReplenishmentNeeded(
    Guid EventId,
    string Sku,
    string WarehouseCode,
    int AvailableQuantity,
    int ReorderPoint,
    string CorrelationId,
    DateTimeOffset DetectedAt);

/// <summary>An order sitting in <see cref="OrderStatuses.Backordered"/> was cancelled; stop waiting for stock on its behalf.</summary>
public sealed record BackorderCancellationRequested(
    Guid OrderId,
    string CorrelationId,
    DateTimeOffset RequestedAt);
