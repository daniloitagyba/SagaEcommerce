using System.Text;

namespace Storefront.Service;

/// <summary>
/// Storefront.Service is a backend-for-frontend: the browser only talks to
/// this one origin, avoiding CORS and keeping internal cluster addresses
/// out of client-side code. Every route is a thin, generic forward.
///
/// The direct /api/orders passthrough (kept for k6/Pact/the
/// README quickstart, which post straight to it rather than going through
/// StorefrontEndpoints.CheckoutAsync's cart-driven flow) now forwards the
/// caller's own Authorization header instead of minting a service-account
/// one - it used to authenticate as Storefront itself and simply relay
/// whatever customerId the body asserted, the same gap CheckoutAsync had.
/// </summary>
public static class ProxyEndpoints
{
    public static IEndpointRouteBuilder MapProxyEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/api/catalog/{**path}", (string path, HttpRequest request, IHttpClientFactory factory, CancellationToken cancellationToken)
            => ForwardAsync(factory.CreateClient("catalog"), path, request, cancellationToken));

        endpoints.MapGet("/api/cart/{**path}", (string path, HttpRequest request, IHttpClientFactory factory, CancellationToken cancellationToken)
            => ForwardAsync(factory.CreateClient("cart"), path, request, cancellationToken));
        endpoints.MapPut("/api/cart/{**path}", (string path, HttpRequest request, IHttpClientFactory factory, CancellationToken cancellationToken)
            => ForwardAsync(factory.CreateClient("cart"), path, request, cancellationToken));
        endpoints.MapDelete("/api/cart/{**path}", (string path, HttpRequest request, IHttpClientFactory factory, CancellationToken cancellationToken)
            => ForwardAsync(factory.CreateClient("cart"), path, request, cancellationToken));

        endpoints.MapPost("/api/orders", (HttpRequest request, IHttpClientFactory factory, CancellationToken cancellationToken)
            => ForwardOrderAsync(factory.CreateClient("orders"), request, cancellationToken));

        // Order management the frontend needs beyond checkout itself -
        // GET .../{id}, GET .../{id}/history, GET .../summary all fall out
        // of the one wildcard GET below; POST covers .../{id}/cancellation
        // (self-service cancel) and .../{id}/returns (both bodyless or
        // JSON-bodied, ForwardAsync now reads either). Deliberately a
        // wildcard rather than one route per sub-path: Orders.Api owns the
        // actual shape of what's under /orders/, this is just a forward -
        // see ForwardOrdersSubPathAsync for why it isn't inlined here.
        endpoints.MapGet("/api/orders/{**path}", (string path, HttpRequest request, IHttpClientFactory factory, CancellationToken cancellationToken)
            => ForwardOrdersSubPathAsync(path, request, factory, cancellationToken));
        endpoints.MapPost("/api/orders/{**path}", (string path, HttpRequest request, IHttpClientFactory factory, CancellationToken cancellationToken)
            => ForwardOrdersSubPathAsync(path, request, factory, cancellationToken));

        return endpoints;
    }

    // Not private, and not inlined into the two lambdas above: OrdersProxyEndpointTests
    // calls this directly to exercise the exact "orders/" prefixing bug this
    // method exists to fix. The prefix is NOT redundant with the "orders"
    // HttpClient's BaseAddress (http://orders-api-1:8080, no path) - unlike
    // catalog-service/cart-service, whose own routes don't repeat the
    // "catalog"/"cart" segment, Orders.Api's routes genuinely start with
    // "/orders" (MapGroup("/orders") in OrderEndpoints.cs). Forwarding a
    // bare {path} silently dropped it: a request for /api/orders/{id}
    // became GET http://orders-api-1:8080/{id} (no "/orders/"), which
    // orders-api-1 has no route for, so it 404d. Order creation never
    // showed the bug - ForwardOrderAsync below hardcodes "/orders" instead
    // of reconstructing it from a path.
    internal static Task ForwardOrdersSubPathAsync(string path, HttpRequest request, IHttpClientFactory factory, CancellationToken cancellationToken) =>
        ForwardAsync(factory.CreateClient("orders"), $"orders/{path}", request, cancellationToken);

    // Not private: OrdersProxyEndpointTests/FullCheckoutFlowTests call this
    // directly for the same reason WriteResponseAsync below already is.
    internal static async Task ForwardAsync(HttpClient client, string path, HttpRequest request, CancellationToken cancellationToken)
    {
        var target = $"/{path}{request.QueryString}";
        using var upstreamRequest = new HttpRequestMessage(new HttpMethod(request.Method), target);

        if ((HttpMethods.IsPut(request.Method) || HttpMethods.IsPost(request.Method)) && request.ContentLength is > 0)
        {
            using var reader = new StreamReader(request.Body);
            var body = await reader.ReadToEndAsync(cancellationToken);
            upstreamRequest.Content = new StringContent(body, Encoding.UTF8, "application/json");
        }

        // Cart.Service now requires the caller's own token
        // (catalog stays anonymous for GETs, so this is a no-op there).
        var authorization = request.Headers.Authorization.FirstOrDefault();
        if (!string.IsNullOrWhiteSpace(authorization))
        {
            upstreamRequest.Headers.TryAddWithoutValidation("Authorization", authorization);
        }

        using var response = await client.SendAsync(upstreamRequest, cancellationToken);
        await WriteResponseAsync(request.HttpContext, response, cancellationToken);
    }

    private static async Task ForwardOrderAsync(
        HttpClient client,
        HttpRequest request,
        CancellationToken cancellationToken)
    {
        using var reader = new StreamReader(request.Body);
        var body = await reader.ReadToEndAsync(cancellationToken);

        using var upstreamRequest = new HttpRequestMessage(HttpMethod.Post, "/orders")
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json")
        };

        var authorization = request.Headers.Authorization.FirstOrDefault();
        if (!string.IsNullOrWhiteSpace(authorization))
        {
            upstreamRequest.Headers.TryAddWithoutValidation("Authorization", authorization);
        }

        using var response = await client.SendAsync(upstreamRequest, cancellationToken);
        await WriteResponseAsync(request.HttpContext, response, cancellationToken);
    }

    // Not private: StorefrontEndpoints.CheckoutAsync reuses this to relay
    // Orders.Api's response (success or validation/infra failure) verbatim,
    // the same way every route in this file does.
    internal static async Task WriteResponseAsync(HttpContext context, HttpResponseMessage response, CancellationToken cancellationToken)
    {
        var responseBody = await response.Content.ReadAsByteArrayAsync(cancellationToken);

        context.Response.StatusCode = (int)response.StatusCode;
        if (responseBody.Length > 0)
        {
            context.Response.ContentType = response.Content.Headers.ContentType?.ToString() ?? "application/json";
            await context.Response.Body.WriteAsync(responseBody, cancellationToken);
        }
    }
}
