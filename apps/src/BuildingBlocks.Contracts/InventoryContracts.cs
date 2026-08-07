namespace BuildingBlocks;

/// <summary>
/// Milestone 41: reservation command/reply contracts for Inventory.Service.
/// Plain JSON, not schema-registered - same rationale as Milestone 22's
/// PaymentDecisionRequested/Replied: internal, transient request/reply
/// messages, not a domain event other consumers evolve against.
///
/// InventoryReservationRequested must be produced keyed by Sku, not OrderId.
/// Kafka guarantees exactly one consumer instance owns a given partition at
/// a time, and the same key always maps to the same partition - so keying
/// by Sku means every reservation request for a given SKU is handled
/// strictly one-at-a-time by exactly one Inventory.Service replica. That
/// partition ownership is the only thing preventing an oversell race
/// between two requests for the same SKU; Inventory.Service deliberately
/// does not also take a database row lock to enforce it.
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
    // Milestone 74: distinguishes "the network cannot cover this, wait for
    // a restock" from "the network cannot cover this, give up" - both are
    // Reserved: false, but only the first should stop the saga from
    // cancelling. A stale message from before this field existed
    // deserializes it false, which resolves to the pre-existing behaviour:
    // cancel outright, exactly as it always did.
    bool Backordered = false);

/// <summary>
/// Milestone 43: the two ways a reservation can be settled once made -
/// Commit turns a temporary hold into a permanent deduction (payment
/// approved), Release gives the held stock back to Available (payment
/// declined - this is the saga's compensating transaction). Both carry
/// the SAME ReservationId as the original InventoryReservationRequested,
/// not a new one: they act ON that reservation, not a new independent
/// operation, and reusing the id lets the whole lifecycle be correlated
/// in logs. Produced keyed by Sku for the same partition-ownership
/// reason as the original reservation request.
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
///
/// Until now <c>ReorderPoint</c> was a column nobody read - the model
/// carried a number that implied stock gets replenished and then never
/// acted on it. This is the signal a replenishment process would consume.
/// Nothing in this lab does yet, and that is the honest state of it: the
/// event is emitted, durably and transactionally, and the consumer is
/// somebody else's milestone.
///
/// <para>
/// Emitted on the <em>crossing</em>, not on every reservation that finds a
/// warehouse already low. A shop selling briskly from a depleted warehouse
/// would otherwise publish one of these per order, which is how a useful
/// signal becomes a topic everyone filters out.
/// </para>
/// </summary>
public sealed record WarehouseReplenishmentNeeded(
    string Sku,
    string WarehouseCode,
    int AvailableQuantity,
    int ReorderPoint,
    string CorrelationId,
    DateTimeOffset DetectedAt);
