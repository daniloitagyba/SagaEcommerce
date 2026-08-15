namespace Orders.Worker;

public sealed record SagaLineRecord(
    int LineIndex,
    Guid ReservationId,
    string Sku,
    int Quantity,
    bool? Reserved,
    bool? Committed,
    bool? Released);

public sealed record SagaOrchestrationRecord(
    string CorrelationId,
    string CustomerId,
    string PaymentMethod,
    string ShippingPostalPrefix,
    DateTimeOffset RequestedAt,
    string Step,
    decimal Amount,
    string Currency,
    IReadOnlyList<SagaLineRecord> Lines,
    DateTimeOffset? CancellationRequestedAt = null,
    DateTimeOffset? ParkedAt = null);

public sealed record SagaReservationLine(Guid ReservationId, string Sku, int Quantity);

public enum SagaLineOutcomeField
{
    Reserved,
    Committed,
    Released
}

/// <summary>
/// The four steps this saga walks through. Not an enum mapped by EF - the
/// column is a plain string so ad hoc SQL inspection during development
/// doesn't need to decode an integer.
/// </summary>
public static class SagaStep
{
    public const string ReserveInventory = "ReserveInventory";
    public const string DecidePayment = "DecidePayment";
    public const string CommitInventory = "CommitInventory";
    public const string ReleaseInventory = "ReleaseInventory";
}
