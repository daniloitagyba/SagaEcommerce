using BuildingBlocks;
using Microsoft.EntityFrameworkCore;
using Orders.Application.Exceptions;
using Orders.Application.Ports;
using Orders.Domain;
using Orders.Infrastructure.Data;
using Polly;
using Polly.Registry;

namespace Orders.Infrastructure.Persistence;

public sealed class EfCustomerRepository(
    OrdersDbContext dbContext,
    ResiliencePipelineProvider<string> pipelineProvider) : ICustomerRepository
{
    private readonly ResiliencePipeline _pipeline = pipelineProvider.GetPipeline(ResilienceExtensions.PostgresPipeline);

    public async Task<Customer> GetOrCreateAsync(string customerId, CancellationToken cancellationToken)
    {
        try
        {
            return await _pipeline.ExecuteAsync(async ct =>
            {
                var existing = await dbContext.Customers.AsNoTracking()
                    .SingleOrDefaultAsync(customer => customer.Id == customerId, ct);

                if (existing is not null)
                {
                    return existing;
                }

                // ON CONFLICT DO NOTHING rather than insert-if-absent: two
                // concurrent first orders from the same customer would both
                // see no row and both insert. The database decides which one
                // wins, and the loser reads the winner's row.
                var createdAt = DateTimeOffset.UtcNow;
                await dbContext.Database.ExecuteSqlInterpolatedAsync(
                    $"""
                    INSERT INTO customers (id, tier, lifetime_spend, completed_order_count, created_at)
                    VALUES ({customerId}, {CustomerTiers.Bronze}, 0, 0, {createdAt})
                    ON CONFLICT (id) DO NOTHING
                    """,
                    ct);

                return await dbContext.Customers.AsNoTracking()
                    .SingleAsync(customer => customer.Id == customerId, ct);
            }, cancellationToken);
        }
        catch (Exception exception) when (ResilienceExtensions.IsInfrastructureFault(exception))
        {
            throw new InfrastructureUnavailableException("PostgreSQL is currently unavailable.", exception);
        }
    }
}
