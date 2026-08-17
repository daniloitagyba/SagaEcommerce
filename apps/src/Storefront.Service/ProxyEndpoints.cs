using System.Net.Http.Headers;

namespace Storefront.Service;

/// <summary>Storefront.Service's backend-for-frontend proxy; every route is a thin, generic forward that streams bodies and forwards an explicit header allowlist.</summary>
public static class ProxyEndpoints
{
    private static readonly string[] ForwardedRequestHeaders = ["Authorization", "Idempotency-Key", "X-Correlation-ID", "Accept"];

    private static readonly string[] ForwardedResponseHeaders =
    [
        "Retry-After", "Location", "ETag", "X-Correlation-ID", "Idempotency-Replayed",
        "X-RateLimit-Limit", "X-RateLimit-Remaining", "X-RateLimit-Distributed-Limit", "X-RateLimit-Distributed-Count"
    ];

    private const long MaxForwardedBodyBytes = 5 * 1024 * 1024;

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
        endpoints.MapPost("/api/cart/{**path}", (string path, HttpRequest request, IHttpClientFactory factory, CancellationToken cancellationToken)
            => ForwardAsync(factory.CreateClient("cart"), path, request, cancellationToken));

        endpoints.MapPost("/api/orders", (HttpRequest request, IHttpClientFactory factory, CancellationToken cancellationToken)
            => ForwardOrderAsync(factory.CreateClient("orders"), request, cancellationToken));

        endpoints.MapGet("/api/orders/{**path}", (string path, HttpRequest request, IHttpClientFactory factory, CancellationToken cancellationToken)
            => ForwardOrdersSubPathAsync(path, request, factory, cancellationToken));
        endpoints.MapPost("/api/orders/{**path}", (string path, HttpRequest request, IHttpClientFactory factory, CancellationToken cancellationToken)
            => ForwardOrdersSubPathAsync(path, request, factory, cancellationToken));

        return endpoints;
    }

    internal static Task ForwardOrdersSubPathAsync(string path, HttpRequest request, IHttpClientFactory factory, CancellationToken cancellationToken) =>
        ForwardAsync(factory.CreateClient("orders"), $"orders/{path}", request, cancellationToken);

    internal static async Task ForwardAsync(HttpClient client, string path, HttpRequest request, CancellationToken cancellationToken)
    {
        var target = $"/{path}{request.QueryString}";
        using var upstreamRequest = new HttpRequestMessage(new HttpMethod(request.Method), target);

        if ((HttpMethods.IsPut(request.Method) || HttpMethods.IsPost(request.Method)) && request.ContentLength is > 0)
        {
            if (request.ContentLength > MaxForwardedBodyBytes)
            {
                request.HttpContext.Response.StatusCode = StatusCodes.Status413PayloadTooLarge;
                return;
            }

            upstreamRequest.Content = new StreamContent(request.Body);
            upstreamRequest.Content.Headers.ContentType = ParseContentType(request.ContentType) ?? new MediaTypeHeaderValue("application/json");
        }

        CopyForwardedRequestHeaders(request, upstreamRequest);

        using var response = await client.SendAsync(upstreamRequest, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        await WriteResponseAsync(request.HttpContext, response, cancellationToken);
    }

    private static async Task ForwardOrderAsync(
        HttpClient client,
        HttpRequest request,
        CancellationToken cancellationToken)
    {
        if (request.ContentLength > MaxForwardedBodyBytes)
        {
            request.HttpContext.Response.StatusCode = StatusCodes.Status413PayloadTooLarge;
            return;
        }

        using var upstreamRequest = new HttpRequestMessage(HttpMethod.Post, "/orders")
        {
            Content = new StreamContent(request.Body)
        };
        upstreamRequest.Content.Headers.ContentType = ParseContentType(request.ContentType) ?? new MediaTypeHeaderValue("application/json");

        CopyForwardedRequestHeaders(request, upstreamRequest);

        using var response = await client.SendAsync(upstreamRequest, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        await WriteResponseAsync(request.HttpContext, response, cancellationToken);
    }

    /// <summary>Copies the forwarded-header allowlist onto an outbound request; <paramref name="excludeHeaderName"/> skips one the caller computes itself.</summary>
    internal static void CopyForwardedRequestHeaders(HttpRequest request, HttpRequestMessage upstreamRequest, string? excludeHeaderName = null)
    {
        foreach (var headerName in ForwardedRequestHeaders)
        {
            if (string.Equals(headerName, excludeHeaderName, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var value = request.Headers[headerName].ToString();
            if (!string.IsNullOrWhiteSpace(value))
            {
                upstreamRequest.Headers.TryAddWithoutValidation(headerName, value);
            }
        }
    }

    private static MediaTypeHeaderValue? ParseContentType(string? contentType) =>
        !string.IsNullOrWhiteSpace(contentType) && MediaTypeHeaderValue.TryParse(contentType, out var parsed)
            ? parsed
            : null;

    internal static async Task WriteResponseAsync(HttpContext context, HttpResponseMessage response, CancellationToken cancellationToken)
    {
        context.Response.StatusCode = (int)response.StatusCode;

        if (response.Content.Headers.ContentType is not null)
        {
            context.Response.ContentType = response.Content.Headers.ContentType.ToString();
        }

        foreach (var headerName in ForwardedResponseHeaders)
        {
            if (response.Headers.TryGetValues(headerName, out var values))
            {
                context.Response.Headers[headerName] = values.ToArray();
            }
        }

        await response.Content.CopyToAsync(context.Response.Body, cancellationToken);
    }
}
