using Orders.Application.Ports;
using Orders.Domain;

namespace Orders.Application.UseCases.ListOrderSummaries;

/// <summary>
/// Lists order summaries for a caller.
/// </summary>
public sealed class ListOrderSummariesHandler(IOrderSummaryRepository repository)
{
    private const int MaximumLimit = 100;

    /// <summary>
    /// Gets the maximum page size.
    /// </summary>
    public static int ClampLimit(int? limit) => Math.Clamp(limit ?? 20, 1, MaximumLimit);

    public Task<IReadOnlyList<OrderSummary>> HandleAsync(
        string? status,
        string? customerId,
        OrderSummaryCursor? cursor,
        int? limit,
        CancellationToken cancellationToken)
    {
        return repository.ListAsync(status, customerId, cursor, ClampLimit(limit), cancellationToken);
    }
}
