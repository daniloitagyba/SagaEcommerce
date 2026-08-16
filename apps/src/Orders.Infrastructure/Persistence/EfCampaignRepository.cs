using BuildingBlocks;
using Microsoft.EntityFrameworkCore;
using Orders.Application.Exceptions;
using Orders.Application.Ports;
using Orders.Domain;
using Orders.Infrastructure.Data;
using Polly;
using Polly.Registry;

namespace Orders.Infrastructure.Persistence;

public sealed class EfCampaignRepository(
    OrdersDbContext dbContext,
    ResiliencePipelineProvider<string> pipelineProvider) : ICampaignRepository
{
    private readonly ResiliencePipeline _pipeline = pipelineProvider.GetPipeline(ResilienceExtensions.PostgresPipeline);

    public async Task<CampaignSnapshot?> FindBestActiveAsync(
        decimal subtotal,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        try
        {
            return await _pipeline.ExecuteAsync(async ct =>
            {
                // Filtered and ordered in the database, not in memory: the
                // table is expected to hold a handful of live campaigns at
                // once, not thousands, so this is a cheap index-friendly
                // scan rather than a premature optimisation.
                var campaign = await dbContext.PromotionCampaigns
                    .AsNoTracking()
                    .Where(item => item.ValidFrom <= now && item.ValidUntil > now)
                    .Where(item => item.MinimumOrderAmount <= subtotal)
                    .Where(item => item.BudgetRemaining >= item.DiscountAmount)
                    .OrderByDescending(item => item.DiscountAmount)
                    .ThenBy(item => item.Code)
                    .FirstOrDefaultAsync(ct);

                return campaign is null
                    ? null
                    : new CampaignSnapshot(
                        campaign.Code,
                        campaign.Description,
                        campaign.DiscountAmount,
                        campaign.ValidFrom,
                        campaign.ValidUntil,
                        campaign.MinimumOrderAmount,
                        campaign.ExclusivityGroup,
                        campaign.BudgetRemaining);
            }, cancellationToken);
        }
        catch (Exception exception) when (ResilienceExtensions.IsInfrastructureFault(exception))
        {
            throw new InfrastructureUnavailableException("PostgreSQL is currently unavailable.", exception);
        }
    }
}
