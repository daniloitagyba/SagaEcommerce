using BuildingBlocks;
using Microsoft.EntityFrameworkCore;
using Orders.Application.Exceptions;
using Orders.Application.Ports;
using Orders.Domain;
using Orders.Infrastructure.Data;
using Polly;
using Polly.Registry;

namespace Orders.Infrastructure.Persistence;

public sealed class EfOrderSummaryRepository(
    OrdersDbContext dbContext,
    ResiliencePipelineProvider<string> pipelineProvider) : IOrderSummaryRepository
{
    private readonly ResiliencePipeline _pipeline = pipelineProvider.GetPipeline(ResilienceExtensions.PostgresPipeline);

    public async Task<IReadOnlyList<OrderSummary>> ListAsync(
        string? status,
        string? customerId,
        OrderSummaryCursor? cursor,
        int limit,
        CancellationToken cancellationToken)
    {
        try
        {
            return await _pipeline.ExecuteAsync(async ct =>
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
                    query = query.Where(item =>
                        item.ProjectedAt < after.ProjectedAt
                        || (item.ProjectedAt == after.ProjectedAt && item.OrderId != after.OrderId));
                }

                return await query
                    .OrderByDescending(item => item.ProjectedAt)
                    .ThenByDescending(item => item.OrderId)
                    .Take(limit)
                    .ToListAsync(ct);
            }, cancellationToken);
        }
        catch (Exception exception) when (ResilienceExtensions.IsInfrastructureFault(exception))
        {
            throw new InfrastructureUnavailableException("PostgreSQL is currently unavailable.", exception);
        }
    }
}
