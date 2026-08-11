namespace Orders.Infrastructure.Data;

/// <summary>
/// Durable ownership of an idempotency key. It is inserted before Order and
/// Outbox inside their transaction, so a committed key always identifies a
/// committed order and a rolled-back order never consumes the key.
/// </summary>
public sealed class OrderIdempotencyRecord
{
    public string CustomerId { get; init; } = string.Empty;

    public string IdempotencyKey { get; init; } = string.Empty;

    public string RequestHash { get; init; } = string.Empty;

    public Guid OrderId { get; init; }

    public DateTimeOffset CreatedAt { get; init; }
}
