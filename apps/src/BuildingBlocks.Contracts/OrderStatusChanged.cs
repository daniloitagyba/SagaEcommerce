namespace BuildingBlocks;

/// <summary>
/// Broadcasts any post-creation status transition AdvanceFulfillmentHandler
/// commits (warehouse-driven or self-service) - the read-model projection
/// otherwise only ever learns an order's status from OrderCreated/PaymentDecided,
/// so a shopper-initiated cancellation (or any fulfilment move) never reached it.
///
/// Every producer (Orders.Worker's OrderStatusStore, Orders.Infrastructure's
/// EfOrderStatusRepository and EfOrderReturnRepository) stamps Version from
/// the same Postgres sequence, order_event_version_seq, in the very
/// transaction that wins the order's status CAS - a single cross-process
/// monotonic counter the projection's ordering guard compares instead of
/// OccurredAt, which is a wall clock each producer stamps independently and
/// so can arrive out of order under ordinary clock skew between pods. See
/// OrderProjectionStore.UpsertDecisionSql's own comment.
/// </summary>
public sealed record OrderStatusChanged(
    Guid EventId,
    Guid OrderId,
    string Status,
    DateTimeOffset OccurredAt,
    string CorrelationId,
    long Version);
