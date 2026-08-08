using Orders.Application.Ports;
using Orders.Domain;

namespace Orders.Application.UseCases.ListOrderSummaries;

/// <summary>
/// Caller.CustomerId scopes the listing to one shopper's own
/// orders; only an admin caller may pass a null customerId through to an
/// unfiltered, cross-customer listing (still status-filterable, still
/// paginated) - Orders.Api decides which of the two this request gets, not
/// this handler, since only the transport layer knows whether the caller
/// is an admin.
/// </summary>
public sealed class ListOrderSummariesHandler(IOrderSummaryRepository repository)
{
    private const int MaximumLimit = 100;

    /// <summary>
    /// Public so Orders.Api can tell whether a page it received was
    /// clamped, without duplicating the clamp bound - a full page against
    /// the caller's raw (unclamped) requested limit is not proof there is
    /// no next page if the actual query ran against a smaller, clamped one.
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
