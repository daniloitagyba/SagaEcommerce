using Microsoft.EntityFrameworkCore;
using Orders.Application.Ports;
using Orders.Domain;
using Orders.Infrastructure.Data;

namespace Orders.Infrastructure.Persistence;

public sealed class EfOrderSummaryRepository(OrdersDbContext dbContext) : IOrderSummaryRepository
{
    public async Task<IReadOnlyList<OrderSummary>> ListAsync(
        string? status,
        string? customerId,
        OrderSummaryCursor? cursor,
        int limit,
        CancellationToken cancellationToken)
    {
        var query = dbContext.OrderSummaries.AsNoTracking();
        if (!string.IsNullOrWhiteSpace(status))
        {
            query = query.Where(item => item.Status == status);
        }

        if (!string.IsNullOrWhiteSpace(customerId))
        {
            query = query.Where(item => item.CustomerId == customerId);
        }

        if (cursor is { } after)
        {
            // Strictly after the named row. Plain equality/inequality
            // rather than Guid.CompareTo, which not every EF provider
            // translates to SQL - a query that throws at request time on a
            // second page is worse than pagination that is merely
            // imprecise across a same-millisecond tie, which ThenByDescending
            // below still orders deterministically within the result set itself.
            query = query.Where(item =>
                item.ProjectedAt < after.ProjectedAt
                || (item.ProjectedAt == after.ProjectedAt && item.OrderId != after.OrderId));
        }

        return await query
            .OrderByDescending(item => item.ProjectedAt)
            .ThenByDescending(item => item.OrderId)
            .Take(limit)
            .ToListAsync(cancellationToken);
    }
}
