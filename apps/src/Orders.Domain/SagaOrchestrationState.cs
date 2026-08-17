namespace Orders.Domain;

/// <summary>Durable state for the orchestrated saga's in-flight requests, one row per OrderId; Step says which reply is expected.</summary>
public sealed class SagaOrchestrationState
{
    private SagaOrchestrationState()
    {
    }

    public Guid OrderId { get; private set; }

    public string CorrelationId { get; private set; } = string.Empty;

    /// <summary>Carried through so the orchestrated payment step can issue PaymentDecisionRequested at step 2 without the original OrderCreated event.</summary>
    public string CustomerId { get; private set; } = string.Empty;

    /// <summary>Carried through for the same reason as CustomerId.</summary>
    public string PaymentMethod { get; private set; } = string.Empty;

    /// <summary>Carried through so the ADDRESS_MISMATCH risk signal is available at step 2.</summary>
    public string ShippingPostalPrefix { get; private set; } = string.Empty;

    public DateTimeOffset RequestedAt { get; private set; }

    public string Step { get; private set; } = string.Empty;

    public decimal Amount { get; private set; }

    public string Currency { get; private set; } = string.Empty;

    /// <summary>Set when an order still carrying this saga row is cancelled through a path outside the saga itself; null otherwise.</summary>
    public DateTimeOffset? CancellationRequestedAt { get; private set; }

    /// <summary>Set when any line of this order's reservation comes back Backordered while the row waits at the ReserveInventory step for a restock; excludes the row from that step's timeout until every line answers.</summary>
    public DateTimeOffset? ParkedAt { get; private set; }
}
