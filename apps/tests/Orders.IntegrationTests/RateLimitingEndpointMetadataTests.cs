extern alias OrdersApi;

using Microsoft.AspNetCore.RateLimiting;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using RateLimitingExtensions = OrdersApi::Orders.Api.RateLimiting.RateLimitingExtensions;

namespace Orders.IntegrationTests;

/// <summary>
/// Three side-effecting writes (cancellation, returns, fulfillment) used to
/// carry no local RequireRateLimiting metadata at all, while every read did
/// - each /orders route file called RequireRateLimiting on its own MapGet/
/// MapPost, and three of them simply never did. Program.cs now applies it
/// once, to a single MapGroup("/orders") every route file registers onto -
/// this reads the real ASP.NET Core route table back out of the running
/// app (not the source files) so a new endpoint added outside that group
/// fails this test rather than silently repeating the drift.
/// </summary>
public sealed class RateLimitingEndpointMetadataTests : IClassFixture<OrdersApiFactory>
{
    private readonly OrdersApiFactory _factory;

    public RateLimitingEndpointMetadataTests(OrdersApiFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public void EveryOrdersRouteCarriesTheLocalRateLimiter()
    {
        var dataSource = _factory.Services.GetRequiredService<EndpointDataSource>();
        var ordersEndpoints = dataSource.Endpoints
            .OfType<RouteEndpoint>()
            .Where(endpoint => endpoint.RoutePattern.RawText is { } text
                && text.StartsWith("/orders", StringComparison.Ordinal))
            .ToList();

        // Guards the rule below from passing trivially if every route were renamed or moved off "/orders".
        Assert.True(
            ordersEndpoints.Count >= 7,
            $"Expected at least 7 /orders endpoints, found {ordersEndpoints.Count} - a route may have moved without this test noticing.");

        var missing = ordersEndpoints
            .Where(endpoint => endpoint.Metadata.GetMetadata<EnableRateLimitingAttribute>() is not { PolicyName: RateLimitingExtensions.OrdersPolicy })
            .Select(endpoint => endpoint.RoutePattern.RawText)
            .ToList();

        Assert.True(
            missing.Count == 0,
            $"{string.Join(", ", missing)} do(es) not carry RequireRateLimiting(RateLimitingExtensions.OrdersPolicy) - every /orders route must be registered onto Program.cs's shared MapGroup, not its own.");
    }
}
