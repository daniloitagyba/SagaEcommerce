namespace Orders.Infrastructure.Data;

/// <summary>Durable ownership of an idempotency key, inserted in the same transaction as Order and Outbox so a committed key always identifies a committed order.</summary>
public sealed class OrderIdempotencyRecord
{
    public string CustomerId { get; init; } = string.Empty;

    public string IdempotencyKey { get; init; } = string.Empty;

    public string RequestHash { get; init; } = string.Empty;

    public Guid OrderId { get; init; }

    public DateTimeOffset CreatedAt { get; init; }
}
