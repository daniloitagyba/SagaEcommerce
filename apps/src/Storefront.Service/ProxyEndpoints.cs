using System.Net.Http.Headers;

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
///
/// Both directions stream rather than buffer the whole body into memory
/// (StreamContent/CopyToAsync, not ReadToEndAsync/ReadAsByteArrayAsync),
/// and both forward an explicit header allowlist rather than only
/// Authorization - see ForwardedRequestHeaders/ForwardedResponseHeaders'
/// own comments and docs/roadmap-milestones-91-99.md, "the BFF proxy
/// loses headers, and buffers every body in memory".
/// </summary>
public static class ProxyEndpoints
{
    // Everything a downstream service's own response might carry that the
    // browser genuinely needs to see: Idempotency-Key so
    // Orders.Api's durable idempotency actually applies through this
    // proxy (it was silently dropped before, on the one route - the
    // direct /api/orders passthrough - that doesn't already set it
    // itself); X-Correlation-ID so a caller-supplied trace id survives the
    // hop instead of Orders.Api's CorrelationIdMiddleware always minting a
    // fresh one; Accept so content negotiation isn't silently forced to
    // whatever the upstream client defaults to.
    private static readonly string[] ForwardedRequestHeaders = ["Authorization", "Idempotency-Key", "X-Correlation-ID", "Accept"];

    // Retry-After (DistributedRateLimitingMiddleware's 429s),
    // Idempotency-Replayed (OrderEndpoints' idempotent-replay signal),
    // X-Correlation-ID (echoed back by CorrelationIdMiddleware),
    // X-RateLimit-* (both the per-pod and cluster-wide limiters' own
    // headers) - all silently dropped before, since the old
    // WriteResponseAsync only ever copied Content-Type and the raw body.
    private static readonly string[] ForwardedResponseHeaders =
    [
        "Retry-After", "Location", "ETag", "X-Correlation-ID", "Idempotency-Replayed",
        "X-RateLimit-Limit", "X-RateLimit-Remaining", "X-RateLimit-Distributed-Limit", "X-RateLimit-Distributed-Count"
    ];

    // Bounds one forwarded request's memory/stream cost - generous for
    // this lab's JSON payloads (an order with dozens of lines, a cart
    // merge with an offline queue of operations), small enough that a
    // misbehaving or malicious client can't turn this BFF into an
    // unbounded memory sink through it.
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
        // Cart.Service's whole CRDT merge subsystem (CartCrdtState,
        // CartStore.MergeAsync, POST /me/merge) was unreachable from the
        // browser without this - the BFF had GET/PUT/DELETE forwards but no
        // POST, so a client reconciling offline changes on reconnect had no
        // route to reach it through.
        endpoints.MapPost("/api/cart/{**path}", (string path, HttpRequest request, IHttpClientFactory factory, CancellationToken cancellationToken)
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
            if (request.ContentLength > MaxForwardedBodyBytes)
            {
                request.HttpContext.Response.StatusCode = StatusCodes.Status413PayloadTooLarge;
                return;
            }

            // Streamed straight through, not buffered into a string first -
            // this request's body never has to fit in memory twice (once
            // as the incoming stream, once as the copied string) just to
            // be relayed unchanged.
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

    /// <summary>
    /// Internal, not private: StorefrontEndpoints.CheckoutAsync - the one
    /// route that actually creates orders and moves money, not a generic
    /// passthrough - builds its own outbound requests by hand rather than
    /// going through ForwardRequestAsync/ForwardOrderAsync above, and needs
    /// this same allowlist so a caller-supplied X-Correlation-ID survives
    /// the checkout path exactly as it does every other proxied route.
    /// <paramref name="excludeHeaderName"/> lets a caller that computes its
    /// own value for one header (CheckoutAsync's deterministic
    /// Idempotency-Key) skip copying the client-supplied one, instead of
    /// ending up with two values for the same header.
    /// </summary>
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

    // Not private: StorefrontEndpoints.CheckoutAsync reuses this to relay
    // Orders.Api's response (success or validation/infra failure) verbatim,
    // the same way every route in this file does.
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

        // Streamed straight through, not buffered into a byte array first -
        // the same reasoning as the request side above.
        await response.Content.CopyToAsync(context.Response.Body, cancellationToken);
    }
}
