using BuildingBlocks;

namespace Orders.Api.Middleware;

public static class CorrelationContext
{
    public static string GetCorrelationId(this HttpContext context)
    {
        return context.Items[MessagingHeaders.CorrelationId]?.ToString() ?? Guid.NewGuid().ToString("N");
    }
}
