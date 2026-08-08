using Orders.Domain;

namespace Orders.Application.Ports;

/// <summary>
/// Where to resume a keyset-paginated listing from - the
/// (ProjectedAt, OrderId) of the last row the caller already saw, since
/// ProjectedAt alone is not unique enough to order by (a burst of
/// projections can share a timestamp at this precision).
/// </summary>
public sealed record OrderSummaryCursor(DateTimeOffset ProjectedAt, Guid OrderId);

public interface IOrderSummaryRepository
{
    /// <summary>
    /// CustomerId narrows to one shopper's orders (null only
    /// for an admin listing across everyone); cursor resumes strictly
    /// after the row it names, ordered the same way the unfiltered listing
    /// already was (ProjectedAt descending, newest first).
    /// </summary>
    Task<IReadOnlyList<OrderSummary>> ListAsync(
        string? status,
        string? customerId,
        OrderSummaryCursor? cursor,
        int limit,
        CancellationToken cancellationToken);
}
