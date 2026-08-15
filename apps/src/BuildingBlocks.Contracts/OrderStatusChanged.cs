namespace BuildingBlocks;

/// <summary>
/// Broadcasts any post-creation status transition AdvanceFulfillmentHandler
/// commits (warehouse-driven or self-service) - the read-model projection
/// otherwise only ever learns an order's status from OrderCreated/PaymentDecided,
/// so a shopper-initiated cancellation (or any fulfilment move) never reached it.
/// </summary>
public sealed record OrderStatusChanged(
    Guid EventId,
    Guid OrderId,
    string Status,
    DateTimeOffset OccurredAt,
    string CorrelationId);
