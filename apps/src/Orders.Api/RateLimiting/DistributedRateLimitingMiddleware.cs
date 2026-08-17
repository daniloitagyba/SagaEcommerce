using System.Globalization;
using BuildingBlocks;
using Orders.Api.Authorization;
using Orders.Infrastructure.RateLimiting;

namespace Orders.Api.RateLimiting;

/// <summary>The cluster-wide rate limiter counterpart to the per-pod token bucket, scoped to /orders.</summary>
public sealed class DistributedRateLimitingMiddleware(RequestDelegate next, RedisSlidingWindowRateLimiter limiter)
{
    private const string RateLimitKeyPrefix = "orders:ratelimit:distributed";

    public async Task InvokeAsync(HttpContext context)
    {
        if (!context.Request.Path.StartsWithSegments("/orders"))
        {
            await next(context);
            return;
        }

        var rateLimitKey = $"{RateLimitKeyPrefix}:{CallerKey(context)}";
        var decision = await limiter.TryAcquireAsync(rateLimitKey, context.RequestAborted);

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

    /// <summary>Resolves the rate-limit bucket key: customer id, else service-account client_id, else "anonymous".</summary>
    private static string CallerKey(HttpContext context) =>
        context.GetCustomerId()
            ?? context.User.FindFirst("azp")?.Value
            ?? "anonymous";
}
