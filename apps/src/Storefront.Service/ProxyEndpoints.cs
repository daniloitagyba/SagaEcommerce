using System.Text;

namespace Storefront.Service;

/// <summary>
/// Storefront.Service is a backend-for-frontend: the browser only talks to
/// this one origin, avoiding CORS and keeping internal cluster addresses
/// out of client-side code. Every route is a thin, generic forward.
///
/// Milestone 83: the direct /api/orders passthrough (kept for k6/Pact/the
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

        return endpoints;
    }

    private static async Task ForwardAsync(HttpClient client, string path, HttpRequest request, CancellationToken cancellationToken)
    {
        var target = $"/{path}{request.QueryString}";
        using var upstreamRequest = new HttpRequestMessage(new HttpMethod(request.Method), target);

        if (HttpMethods.IsPut(request.Method) && request.ContentLength is > 0)
        {
            using var reader = new StreamReader(request.Body);
            var body = await reader.ReadToEndAsync(cancellationToken);
            upstreamRequest.Content = new StringContent(body, Encoding.UTF8, "application/json");
        }

        // Milestone 84: Cart.Service now requires the caller's own token
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
