using BuildingBlocks;
using Microsoft.EntityFrameworkCore;
using Orders.Application.Exceptions;
using Orders.Application.Ports;
using Orders.Domain;
using Orders.Infrastructure.Data;
using Polly;
using Polly.Registry;

namespace Orders.Infrastructure.Persistence;

public sealed class EfCouponRepository(
    OrdersDbContext dbContext,
    ResiliencePipelineProvider<string> pipelineProvider) : ICouponRepository
{
    private readonly ResiliencePipeline _pipeline = pipelineProvider.GetPipeline(ResilienceExtensions.PostgresPipeline);

    public async Task<(CouponSnapshot? Coupon, int CustomerRedemptionCount)> FindAsync(
        string code,
        string customerId,
        CancellationToken cancellationToken)
    {
        var normalized = code.Trim().ToUpperInvariant();

        try
        {
            return await _pipeline.ExecuteAsync(async ct =>
            {
                var coupon = await dbContext.Coupons
                    .AsNoTracking()
                    .SingleOrDefaultAsync(item => item.Code == normalized, ct);

                if (coupon is null)
                {
                    return ((CouponSnapshot?)null, 0);
                }

                var customerRedemptions = await dbContext.CouponRedemptions
                    .AsNoTracking()
                    .CountAsync(
                        item => item.Code == normalized
                            && item.CustomerId == customerId
                            && item.State != CouponRedemptionState.Released,
                        ct);

                var snapshot = new CouponSnapshot(
                    coupon.Code,
                    coupon.Description,
                    coupon.Percentage,
                    coupon.ValidFrom,
                    coupon.ValidUntil,
                    coupon.MinimumOrderAmount,
                    coupon.MaxTotalRedemptions,
                    coupon.MaxPerCustomer,
                    coupon.RedemptionCount);

                return ((CouponSnapshot?)snapshot, customerRedemptions);
            }, cancellationToken);
        }
        catch (Exception exception) when (ResilienceExtensions.IsInfrastructureFault(exception))
        {
            throw new InfrastructureUnavailableException("PostgreSQL is currently unavailable.", exception);
        }
    }
}
