namespace Orders.Domain;

/// <summary>Denormalized read model built asynchronously by the Orders.Worker projector; nullable columns may be transiently unpopulated.</summary>
public sealed class OrderSummary
{
    private OrderSummary()
    {
    }

    /// <summary>Test-only construction seam; production code never builds this by hand.</summary>
    internal static OrderSummary ForTesting(
        Guid orderId,
        string? customerId,
        decimal? amount,
        string? currency,
        string status,
        DateTimeOffset? orderCreatedAt,
        DateTimeOffset? decidedAt,
        DateTimeOffset projectedAt) =>
        new()
        {
            OrderId = orderId,
            CustomerId = customerId,
            Amount = amount,
            Currency = currency,
            Status = status,
            OrderCreatedAt = orderCreatedAt,
            DecidedAt = decidedAt,
            ProjectedAt = projectedAt
        };

    public Guid OrderId { get; private set; }

    public string? CustomerId { get; private set; }

    public decimal? Amount { get; private set; }

    public string? Currency { get; private set; }

    public string Status { get; private set; } = string.Empty;

    public DateTimeOffset? OrderCreatedAt { get; private set; }

    public DateTimeOffset? DecidedAt { get; private set; }

    public DateTimeOffset ProjectedAt { get; private set; }
}
