namespace BuildingBlocks;

public sealed record OrderCreated(
    Guid EventId,
    Guid OrderId,
    string CustomerId,
    decimal Amount,
    string Currency,
    DateTimeOffset OccurredAt,
    string CorrelationId,
    int SchemaVersion = 1);
