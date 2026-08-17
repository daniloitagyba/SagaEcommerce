namespace BuildingBlocks;

/// <summary>Broadcasts any post-creation order status transition so the read-model projection stays current.</summary>
public sealed record OrderStatusChanged(
    Guid EventId,
    Guid OrderId,
    string Status,
    DateTimeOffset OccurredAt,
    string CorrelationId,
    long Version);
