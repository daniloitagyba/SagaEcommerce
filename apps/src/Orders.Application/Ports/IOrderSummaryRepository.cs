using Orders.Domain;

namespace Orders.Application.Ports;

public sealed record OrderSummaryCursor(DateTimeOffset ProjectedAt, Guid OrderId);

public interface IOrderSummaryRepository
{
    Task<IReadOnlyList<OrderSummary>> ListAsync(
        string? status,
        string? customerId,
        OrderSummaryCursor? cursor,
        int limit,
        CancellationToken cancellationToken);
}
