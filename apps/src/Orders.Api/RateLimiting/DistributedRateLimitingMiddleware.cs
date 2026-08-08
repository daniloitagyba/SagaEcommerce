using System.Globalization;
using BuildingBlocks;
using Orders.Infrastructure.RateLimiting;

namespace Orders.Api.RateLimiting;

/// <summary>
/// The cluster-wide counterpart to the per-pod
/// token bucket (applied via app.UseRateLimiter() just before this runs -
/// a request has to pass the cheap local check first). Scoped to /orders
/// only, matching RateLimitingExtensions.OrdersPolicy's endpoint group.
/// </summary>
public sealed class DistributedRateLimitingMiddleware(RequestDelegate next, RedisSlidingWindowRateLimiter limiter)
{
    private const string RateLimitKey = "orders:ratelimit:distributed";

    public async Task InvokeAsync(HttpContext context)
    {
        if (!context.Request.Path.StartsWithSegments("/orders"))
        {
            await next(context);
            return;
        }

        var decision = await limiter.TryAcquireAsync(RateLimitKey, context.RequestAborted);

        context.Response.Headers["X-RateLimit-Distributed-Limit"] = decision.Limit.ToString(CultureInfo.InvariantCulture);
        if (decision.Count >= 0)
        {
            context.Response.Headers["X-RateLimit-Distributed-Count"] = decision.Count.ToString(CultureInfo.InvariantCulture);
        }

        if (!decision.Allowed)
        {
            OrdersTelemetry.RecordDistributedRateLimited();
            context.Response.StatusCode = StatusCodes.Status429TooManyRequests;
            context.Response.Headers["Retry-After"] = "1";
            await context.Response.WriteAsJsonAsync(new
            {
                title = "Too Many Requests",
                status = StatusCodes.Status429TooManyRequests,
                detail = "The orders API's cluster-wide rate limit is shedding load; retry after the indicated delay."
            });
            return;
        }

        await next(context);
    }
}
