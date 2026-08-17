namespace Orders.Domain;

/// <summary>An append-only domain event, the unit of the event store, ordered by a Postgres-assigned bigserial Id.</summary>
public sealed class OrderEvent
{
    private OrderEvent()
    {
    }

    /// <summary>Test-only construction seam; production code never builds this by hand.</summary>
    internal static OrderEvent ForTesting(long id, Guid orderId, string eventType, string payload, DateTimeOffset occurredAt) =>
        new()
        {
            Id = id,
            OrderId = orderId,
            EventType = eventType,
            Payload = payload,
            OccurredAt = occurredAt
        };

    public long Id { get; private set; }

    public Guid OrderId { get; private set; }

    public string EventType { get; private set; } = string.Empty;

    public string Payload { get; private set; } = string.Empty;

    public DateTimeOffset OccurredAt { get; private set; }
}
