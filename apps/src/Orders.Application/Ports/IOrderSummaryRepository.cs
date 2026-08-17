using Orders.Domain;

namespace Orders.Application.Ports;

/// <summary>
/// Identifies an order summary page position.
/// </summary>
public sealed record OrderSummaryCursor(DateTimeOffset ProjectedAt, Guid OrderId);

public interface IOrderSummaryRepository
{
    /// <summary>
    /// Lists filtered order summaries.
    /// </summary>
    Task<IReadOnlyList<OrderSummary>> ListAsync(
        string? status,
        string? customerId,
        OrderSummaryCursor? cursor,
        int limit,
        CancellationToken cancellationToken);
}
