using System.Globalization;
using BuildingBlocks;
using Orders.Api.Authorization;
using Orders.Infrastructure.RateLimiting;

namespace Orders.Api.RateLimiting;

/// <summary>
/// The cluster-wide counterpart to the per-pod
/// token bucket (applied via app.UseRateLimiter() before this runs -
/// a request has to pass the cheap local check first). Scoped to /orders
/// only, matching RateLimitingExtensions.OrdersPolicy's endpoint group.
///
/// Runs after UseAuthentication (see Program.cs's pipeline order) so it can
/// key the bucket by caller instead of one shared key for every request -
/// a single "orders:ratelimit:distributed" key meant one abusive or buggy
/// client exhausted the whole cluster's budget and 429'd everyone else.
/// </summary>
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

    /// <summary>
    /// The shopper's own customer id when there is one; the client_id
    /// (azp) for a service-account token with no human behind it (a
    /// trusted backend caller like Orders.Worker's anti-entropy sweeper -
    /// still its own bucket, not lumped in with every shopper); "anonymous"
    /// only as a last resort, since every route this middleware guards
    /// requires authentication already.
    /// </summary>
    private static string CallerKey(HttpContext context) =>
        context.GetCustomerId()
            ?? context.User.FindFirst("azp")?.Value
            ?? "anonymous";
}
