namespace Orders.Domain;

/// <summary>
/// Milestone 36: durable state for the Milestone 22 orchestrated saga's
/// in-flight requests, replacing an in-memory ConcurrentDictionary that
/// didn't survive a pod restart or a KEDA scale-in - the exact kind of
/// event this lab's chaos testing induces on purpose. EF Core owns this
/// table's schema (migrations) only; runtime reads/writes go through raw
/// Npgsql in Orders.Worker's SagaOrchestrationStore, matching the existing
/// OrderEvent/order_events pattern.
///
/// Milestone 43: extended from "one pending request" to "one pending step
/// of a multi-step saga". At most one reply is ever outstanding per order
/// at a time (the steps are strictly sequential), so this stays a single
/// row per OrderId - Step just says which reply is currently expected.
/// ReservationId/Sku/Quantity are set once at the first step and carried
/// through unchanged, since Commit/Release both act on that same
/// reservation.
/// </summary>
public sealed class SagaOrchestrationState
{
    private SagaOrchestrationState()
    {
    }

    public Guid OrderId { get; private set; }

    public string CorrelationId { get; private set; } = string.Empty;

    /// <summary>
    /// Milestone 66: carried through the saga so the orchestrated payment
    /// step can ask the same risk questions the choreographed one can -
    /// "has this customer bought before?", "is this order out of character
    /// for them?". PaymentDecisionRequested has no other way to learn who
    /// the customer is, since it is issued at step 2 from this row rather
    /// than from the original OrderCreated event.
    /// </summary>
    public string CustomerId { get; private set; } = string.Empty;

    /// <summary>Milestone 68: carried for the same reason as CustomerId - the decision request needs it and OrderCreated is long gone by step 2.</summary>
    public string PaymentMethod { get; private set; } = string.Empty;

    /// <summary>Milestone 73: same carry-through again - the ADDRESS_MISMATCH risk signal needs it at step 2.</summary>
    public string ShippingPostalPrefix { get; private set; } = string.Empty;

    public DateTimeOffset RequestedAt { get; private set; }

    public string Step { get; private set; } = string.Empty;

    public Guid ReservationId { get; private set; }

    public string Sku { get; private set; } = string.Empty;

    public int Quantity { get; private set; }

    public decimal Amount { get; private set; }

    public string Currency { get; private set; } = string.Empty;
}
