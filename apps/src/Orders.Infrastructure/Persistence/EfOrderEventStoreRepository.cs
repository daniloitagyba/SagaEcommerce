using BuildingBlocks;
using Microsoft.EntityFrameworkCore;
using Orders.Application.Exceptions;
using Orders.Application.Ports;
using Orders.Domain;
using Orders.Infrastructure.Data;
using Polly;
using Polly.Registry;

namespace Orders.Infrastructure.Persistence;

public sealed class EfOrderEventStoreRepository(
    OrdersDbContext dbContext,
    ResiliencePipelineProvider<string> pipelineProvider) : IOrderEventStoreRepository
{
    private readonly ResiliencePipeline _pipeline = pipelineProvider.GetPipeline(ResilienceExtensions.PostgresPipeline);

    public async Task<IReadOnlyList<OrderEvent>> ListEventsAsync(Guid orderId, DateTimeOffset? asOf, CancellationToken cancellationToken)
    {
        try
        {
            return await _pipeline.ExecuteAsync(async ct =>
            {
                var query = dbContext.OrderEvents.AsNoTracking().Where(item => item.OrderId == orderId);
                if (asOf is { } boundary)
                {
                    query = query.Where(item => item.OccurredAt <= boundary);
                }

                return await query.OrderBy(item => item.Id).ToListAsync(ct);
            }, cancellationToken);
        }
        catch (Exception exception) when (ResilienceExtensions.IsInfrastructureFault(exception))
        {
            throw new InfrastructureUnavailableException("PostgreSQL is currently unavailable.", exception);
        }
    }
}
