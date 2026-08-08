namespace Orders.Domain;

/// <summary>
/// Milestone 36: durable state for the orchestrated saga's in-flight
/// requests, replacing an in-memory ConcurrentDictionary that didn't
/// survive a pod restart. EF Core owns this table's schema only; runtime
/// reads/writes go through raw Npgsql in SagaOrchestrationStore, matching
/// the OrderEvent/order_events pattern. Milestone 43: one row per OrderId,
/// since only one reply is ever outstanding *per line* at a time - Step
/// says which reply is expected. Milestone 78: ReservationId/Sku/Quantity
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

    /// <summary>Milestone 66: carried through so the orchestrated payment step can ask the same risk questions the choreographed one can - PaymentDecisionRequested is issued at step 2 from this row, not the original OrderCreated event.</summary>
    public string CustomerId { get; private set; } = string.Empty;

    /// <summary>Milestone 68: carried for the same reason as CustomerId - the decision request needs it and OrderCreated is long gone by step 2.</summary>
    public string PaymentMethod { get; private set; } = string.Empty;

    /// <summary>Milestone 73: same carry-through again - the ADDRESS_MISMATCH risk signal needs it at step 2.</summary>
    public string ShippingPostalPrefix { get; private set; } = string.Empty;

    public DateTimeOffset RequestedAt { get; private set; }

    public string Step { get; private set; } = string.Empty;

    public decimal Amount { get; private set; }

    public string Currency { get; private set; } = string.Empty;
}
