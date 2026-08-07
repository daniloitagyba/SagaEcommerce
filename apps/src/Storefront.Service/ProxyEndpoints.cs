using System.Net.Http.Headers;
using System.Text;

namespace Storefront.Service;

/// <summary>
/// Storefront.Service is a backend-for-frontend: the browser only ever
/// talks to this one origin, avoiding CORS entirely and keeping Catalog/
/// Cart/Orders' internal cluster addresses out of client-side code. Every
/// route here is a thin, generic forward - the interesting logic (auth
/// injection for orders-api) lives in ForwardOrderAsync specifically,
/// since Catalog and Cart need no such thing.
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

        endpoints.MapPost("/api/orders", (HttpRequest request, IHttpClientFactory factory, KeycloakTokenProvider tokenProvider, CancellationToken cancellationToken)
            => ForwardOrderAsync(factory.CreateClient("orders"), request, tokenProvider, cancellationToken));

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

        using var response = await client.SendAsync(upstreamRequest, cancellationToken);
        await WriteResponseAsync(request.HttpContext, response, cancellationToken);
    }

    private static async Task ForwardOrderAsync(
        HttpClient client,
        HttpRequest request,
        KeycloakTokenProvider tokenProvider,
        CancellationToken cancellationToken)
    {
        using var reader = new StreamReader(request.Body);
        var body = await reader.ReadToEndAsync(cancellationToken);

        var token = await tokenProvider.GetTokenAsync(cancellationToken);

        using var upstreamRequest = new HttpRequestMessage(HttpMethod.Post, "/orders")
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json")
        };
        upstreamRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

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
