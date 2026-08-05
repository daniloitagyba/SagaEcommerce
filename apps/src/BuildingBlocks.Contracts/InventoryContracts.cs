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
    DateTimeOffset DecidedAt);

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
