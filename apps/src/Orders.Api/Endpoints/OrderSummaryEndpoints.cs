using System.Globalization;
using Orders.Api.Authorization;
using Orders.Api.Contracts;
using Orders.Application.Ports;
using Orders.Application.UseCases.ListOrderSummaries;

namespace Orders.Api.Endpoints;

/// <summary>
/// CQRS query side: reads exclusively from the order_summaries projection
/// built asynchronously by Orders.Worker's projector. Never touches the
/// orders write table or the Redis cache used by GetByIdAsync.
///
/// Scoped to the caller's own orders by default - the mass-disclosure
/// half of the ownership gap a prior audit found (a plain
/// orders:read token could list every customer's orders, unbounded beyond
/// a limit with no offset). An admin caller may still pass customerId to
/// query someone specific, or omit it for the old cross-customer listing.
/// </summary>
public static class OrderSummaryEndpoints
{
    public static IEndpointRouteBuilder MapOrderSummaryEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/summary", ListAsync)
            .WithTags("Orders")
            .RequireAuthorization(OrdersAuthorizationPolicies.Read);

        return endpoints;
    }

    private static async Task<IResult> ListAsync(
        ListOrderSummariesHandler handler,
        HttpContext httpContext,
        string? status,
        string? customerId,
        string? cursor,
        int? limit,
        CancellationToken cancellationToken)
    {
        var isAdmin = httpContext.IsAdmin();
        var effectiveCustomerId = isAdmin ? customerId : httpContext.GetCustomerId();

        if (!TryParseCursor(cursor, out var parsedCursor) && cursor is not null)
        {
            return Results.ValidationProblem(new Dictionary<string, string[]>(StringComparer.Ordinal)
            {
                ["cursor"] = ["cursor is not a value this endpoint issued."]
            });
        }

        var summaries = await handler.HandleAsync(status, effectiveCustomerId, parsedCursor, limit, cancellationToken);

        var response = summaries.Select(item => new OrderSummaryResponse(
            item.OrderId,
            item.CustomerId,
            item.Amount,
            item.Currency,
            item.Status,
            item.OrderCreatedAt,
            item.DecidedAt,
            item.ProjectedAt));

        // A full page might not be the last one - the cursor for the next
        // page names the last row this page actually returned, so the
        // caller doesn't have to know the ordering column itself.
        var last = summaries.Count > 0 ? summaries[^1] : null;
        var nextCursor = last is not null && summaries.Count == ListOrderSummariesHandler.ClampLimit(limit)
            ? FormatCursor(last.ProjectedAt, last.OrderId)
            : null;

        return Results.Ok(new { items = response, nextCursor });
    }

    private static string FormatCursor(DateTimeOffset projectedAt, Guid orderId) =>
        $"{projectedAt.ToString("O", CultureInfo.InvariantCulture)}_{orderId:N}";

    private static bool TryParseCursor(string? cursor, out OrderSummaryCursor? parsed)
    {
        parsed = null;
        if (string.IsNullOrWhiteSpace(cursor))
        {
            return true;
        }

        var parts = cursor.Split('_', 2);
        if (parts.Length != 2
            || !DateTimeOffset.TryParse(parts[0], CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var projectedAt)
            || !Guid.TryParseExact(parts[1], "N", out var orderId))
        {
            return false;
        }

        parsed = new OrderSummaryCursor(projectedAt, orderId);
        return true;
    }
}
