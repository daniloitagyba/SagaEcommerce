namespace BuildingBlocks;

public sealed record PaymentDecided(
    Guid EventId,
    Guid OrderId,
    Guid PaymentId,
    bool Approved,
    decimal Amount,
    string Currency,
    DateTimeOffset OccurredAt,
    string CorrelationId,
    int SchemaVersion = 1);
