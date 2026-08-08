namespace Orders.Domain;

/// <summary>
/// Durable state for the orchestrated saga's in-flight
/// requests, replacing an in-memory ConcurrentDictionary that didn't
/// survive a pod restart. EF Core owns this table's schema only; runtime
/// reads/writes go through raw Npgsql in SagaOrchestrationStore, matching
/// the OrderEvent/order_events pattern. One row per OrderId,
/// since only one reply is ever outstanding *per line* at a time - Step
/// says which reply is expected. ReservationId/Sku/Quantity
/// moved out to <see cref="SagaOrchestrationLine"/> - a multi-line order
/// now has one line row per SKU, not one reservation for the whole order.
/// </summary>
public sealed class SagaOrchestrationState
{
    private SagaOrchestrationState()
    {
    }

    public Guid OrderId { get; private set; }

    public string CorrelationId { get; private set; } = string.Empty;

    /// <summary>Carried through so the orchestrated payment step can ask the same risk questions the choreographed one can - PaymentDecisionRequested is issued at step 2 from this row, not the original OrderCreated event.</summary>
    public string CustomerId { get; private set; } = string.Empty;

    /// <summary>Carried for the same reason as CustomerId - the decision request needs it and OrderCreated is long gone by step 2.</summary>
    public string PaymentMethod { get; private set; } = string.Empty;

    /// <summary>Same carry-through again - the ADDRESS_MISMATCH risk signal needs it at step 2.</summary>
    public string ShippingPostalPrefix { get; private set; } = string.Empty;

    public DateTimeOffset RequestedAt { get; private set; }

    public string Step { get; private set; } = string.Empty;

    public decimal Amount { get; private set; }

    public string Currency { get; private set; } = string.Empty;

    /// <summary>
    /// Set when an order still carrying this saga row (Created or
    /// Backordered - anything that hasn't reached Confirmed yet) is
    /// cancelled through a path outside the saga itself (an operator's
    /// fulfilment endpoint, or a shopper's self-service cancellation).
    /// Null the rest of the time. See OrderSagaReplyConsumer for how a
    /// non-null value changes what the next reply for this order does.
    /// </summary>
    public DateTimeOffset? CancellationRequestedAt { get; private set; }
}
