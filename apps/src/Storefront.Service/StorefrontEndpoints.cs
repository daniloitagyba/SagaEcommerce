using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Options;

namespace Storefront.Service;

internal static partial class StorefrontLog
{
    [LoggerMessage(EventId = 7001, Level = LogLevel.Warning, Message = "{Dependency} unavailable for sku {Sku}, degrading response")]
    public static partial void DependencyUnavailable(ILogger logger, string dependency, string sku, Exception exception);

    [LoggerMessage(EventId = 7002, Level = LogLevel.Error, Message = "Cart {CartId} could not be reached during checkout")]
    public static partial void CartUnavailableDuringCheckout(ILogger logger, string cartId, Exception exception);

    [LoggerMessage(EventId = 7003, Level = LogLevel.Warning, Message = "Order {OrderId} was created but clearing cart {CartId} afterwards failed - the shopper's next visit will still see the items")]
    public static partial void CartClearFailedAfterCheckout(ILogger logger, string cartId, string orderId, Exception exception);
}

/// <summary>
/// Milestone 54: this lab's first genuine BFF fan-out - every other
/// Storefront.Service endpoint besides checkout (ProxyEndpoints) is a 1:1
/// reverse proxy. GET /api/storefront/products/{sku} calls Catalog and
/// Inventory in parallel and waits for both, which means its own tail
/// latency is at least as bad as whichever of the two is having a slow
/// moment - see ProductSummaryOptions for why the Inventory leg can be
/// hedged.
///
/// Milestone 66 added the second: POST /api/storefront/checkout, which
/// turns a cart into an order. See CheckoutAsync's own comment for why
/// that particular orchestration belongs here rather than being pushed
/// onto the browser.
/// </summary>
public static class StorefrontEndpoints
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public static IEndpointRouteBuilder MapStorefrontEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/api/storefront/products/{sku}", GetProductSummaryAsync).WithTags("Storefront");
        endpoints.MapPost("/api/storefront/checkout", CheckoutAsync).WithTags("Storefront");
        return endpoints;
    }

    private static async Task<IResult> GetProductSummaryAsync(
        string sku,
        IHttpClientFactory httpClientFactory,
        IOptions<ProductSummaryOptions> options,
        ILoggerFactory loggerFactory,
        CancellationToken cancellationToken)
    {
        var logger = loggerFactory.CreateLogger("Storefront.Service.StorefrontEndpoints");
        var catalogClient = httpClientFactory.CreateClient("catalog");
        var inventoryClient = httpClientFactory.CreateClient("inventory");
        var hedgeDelayMs = options.Value.HedgeDelayMilliseconds;

        var catalogTask = GetJsonOrNullAsync(catalogClient, $"/products/by-sku/{Uri.EscapeDataString(sku)}", cancellationToken);
        var inventoryTask = hedgeDelayMs > 0
            ? GetHedgedJsonOrNullAsync(inventoryClient, $"/inventory/{Uri.EscapeDataString(sku)}", hedgeDelayMs, cancellationToken)
            : GetJsonOrNullAsync(inventoryClient, $"/inventory/{Uri.EscapeDataString(sku)}", cancellationToken);

        // Milestone 64: awaited separately, not via Task.WhenAll - Inventory
        // is an enrichment of an otherwise-complete Catalog response, not a
        // second required source. Task.WhenAll propagates whichever task
        // faults first and fails the whole request even when Catalog (the
        // side that actually determines whether this SKU exists) answered
        // fine - the exact opposite of what a BFF fanning out to several
        // backends should do on a partial failure. Both legs are still
        // always awaited to completion here, regardless of which one
        // faults first, so neither is ever left unobserved.
        object? product;
        bool catalogUnavailable;
        try
        {
            product = await catalogTask;
            catalogUnavailable = false;
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException)
        {
            StorefrontLog.DependencyUnavailable(logger, "catalog", sku, exception);
            product = null;
            catalogUnavailable = true;
        }

        object? inventory;
        bool inventoryUnavailable;
        try
        {
            inventory = await inventoryTask;
            inventoryUnavailable = false;
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException)
        {
            StorefrontLog.DependencyUnavailable(logger, "inventory", sku, exception);
            inventory = null;
            inventoryUnavailable = true;
        }

        if (catalogUnavailable)
        {
            return Results.Problem(
                detail: "The catalog service is currently unavailable.",
                statusCode: StatusCodes.Status503ServiceUnavailable);
        }

        if (product is null)
        {
            return Results.NotFound(new { message = $"No product with sku '{sku}' was found in the catalog." });
        }

        return Results.Ok(new { product, inventory, degraded = inventoryUnavailable });
    }

    /// <summary>
    /// Fires the primary request; if it hasn't answered within
    /// <paramref name="hedgeDelayMs"/>, fires a second, independent request
    /// to the same logical backend (the K8s Service load-balances new
    /// connections across replicas, so a hedge has a real chance of
    /// landing on a different, non-lagging pod) and takes whichever
    /// finishes first. The loser is cancelled, not left to run to
    /// completion in the background - Milestone 54 measured this matters,
    /// not just tidiness: under a sustained tail of slow responses,
    /// uncancelled losers pile up and contend for the same HttpClient's
    /// connection pool, inflating p99/max even as hedging correctly
    /// improves p95. Safe to cancel here because this is a read-only
    /// GET - cancelling a write mid-flight would risk aborting one the
    /// server is still going to act on, which this endpoint never does.
    /// </summary>
    private static async Task<object?> GetHedgedJsonOrNullAsync(
        HttpClient client,
        string requestUri,
        int hedgeDelayMs,
        CancellationToken cancellationToken)
    {
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var primary = GetJsonOrNullAsync(client, requestUri, linkedCts.Token);
        var delay = Task.Delay(hedgeDelayMs, cancellationToken);

        var firstToFinish = await Task.WhenAny(primary, delay);
        if (firstToFinish == primary)
        {
            return await primary;
        }

        var hedge = GetJsonOrNullAsync(client, requestUri, linkedCts.Token);
        var winner = await Task.WhenAny(primary, hedge);
        var result = await winner;
        await linkedCts.CancelAsync();
        return result;
    }

    private static async Task<object?> GetJsonOrNullAsync(HttpClient client, string requestUri, CancellationToken cancellationToken)
    {
        using var response = await client.GetAsync(requestUri, cancellationToken);
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }

        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<object>(cancellationToken);
    }

    internal sealed record CheckoutRequest(string? CartId, string? CustomerId, string? CouponCode);

    internal sealed record CartSnapshot(IReadOnlyList<CartSnapshotItem> Items);

    internal sealed record CartSnapshotItem(string Sku, int Quantity);

    internal sealed record CheckoutOrderRequest(string CustomerId, IReadOnlyList<CheckoutOrderItem> Items, string? CouponCode);

    internal sealed record CheckoutOrderItem(string Sku, int Quantity);

    /// <summary>
    /// Milestone 66 gave Orders.Api a real checkout - line items priced
    /// server-side against the live catalog - but Cart.Service and
    /// Orders.Api still know nothing of each other; nothing turns "what's
    /// in this shopper's cart" into that call. This is that orchestration,
    /// and it belongs in the BFF rather than the browser for the same
    /// reason ForwardOrderAsync injects the Keycloak token here rather
    /// than shipping a client secret to the client: the browser should
    /// never need to know Orders.Api requires auth, or that turning a cart
    /// into an order is two separate backend calls at all.
    ///
    /// The ordering of those two calls is deliberate, not incidental: the
    /// cart is cleared only after Orders.Api has genuinely accepted the
    /// order. Clearing it first and having the order call then fail would
    /// strand the shopper with an empty cart and nothing purchased - the
    /// one outcome worse than a failed checkout.
    /// </summary>
    internal static async Task CheckoutAsync(
        CheckoutRequest request,
        HttpContext httpContext,
        IHttpClientFactory httpClientFactory,
        KeycloakTokenProvider tokenProvider,
        ILoggerFactory loggerFactory,
        CancellationToken cancellationToken)
    {
        var logger = loggerFactory.CreateLogger("Storefront.Service.StorefrontEndpoints");

        if (string.IsNullOrWhiteSpace(request.CartId) || string.IsNullOrWhiteSpace(request.CustomerId))
        {
            var errors = new Dictionary<string, string[]>(StringComparer.Ordinal);
            if (string.IsNullOrWhiteSpace(request.CartId))
            {
                errors["cartId"] = ["cartId is required."];
            }

            if (string.IsNullOrWhiteSpace(request.CustomerId))
            {
                errors["customerId"] = ["customerId is required."];
            }

            await Results.ValidationProblem(errors).ExecuteAsync(httpContext);
            return;
        }

        var cartClient = httpClientFactory.CreateClient("cart");
        CartSnapshot? cart;
        try
        {
            cart = await cartClient.GetFromJsonAsync<CartSnapshot>(
                $"/carts/{Uri.EscapeDataString(request.CartId)}", JsonOptions, cancellationToken);
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException)
        {
            StorefrontLog.CartUnavailableDuringCheckout(logger, request.CartId, exception);
            await Results.Problem(
                detail: "The cart service is currently unavailable.",
                statusCode: StatusCodes.Status503ServiceUnavailable).ExecuteAsync(httpContext);
            return;
        }

        if (cart is null || cart.Items.Count == 0)
        {
            await Results.ValidationProblem(new Dictionary<string, string[]>(StringComparer.Ordinal)
            {
                ["cart"] = ["The cart is empty."]
            }).ExecuteAsync(httpContext);
            return;
        }

        var orderRequest = new CheckoutOrderRequest(
            request.CustomerId,
            [.. cart.Items.Select(item => new CheckoutOrderItem(item.Sku, item.Quantity))],
            string.IsNullOrWhiteSpace(request.CouponCode) ? null : request.CouponCode);

        var ordersClient = httpClientFactory.CreateClient("orders");
        var token = await tokenProvider.GetTokenAsync(cancellationToken);

        using var upstreamRequest = new HttpRequestMessage(HttpMethod.Post, "/orders")
        {
            Content = JsonContent.Create(orderRequest, options: JsonOptions)
        };
        upstreamRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        using var response = await ordersClient.SendAsync(upstreamRequest, cancellationToken);

        if (response.IsSuccessStatusCode)
        {
            // Buffered so the order id can be read here for the log line
            // below and the body can still be relayed to the client
            // afterwards, untouched - HttpContent only supports reading
            // its stream once unless it has been buffered first.
            await response.Content.LoadIntoBufferAsync(cancellationToken);
            var orderId = await TryReadOrderIdAsync(response, cancellationToken);

            try
            {
                using var clearResponse = await cartClient.DeleteAsync(
                    $"/carts/{Uri.EscapeDataString(request.CartId)}", cancellationToken);
                clearResponse.EnsureSuccessStatusCode();
            }
            catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException)
            {
                // The order is real and already accepted - a failure to
                // clear the cart afterwards must never be reported as a
                // checkout failure, or the shopper will think they were
                // not charged.
                StorefrontLog.CartClearFailedAfterCheckout(logger, request.CartId, orderId ?? "unknown", exception);
            }
        }

        // Whatever Orders.Api decided - created, idempotent replay,
        // validation failure, or infrastructure unavailable - is relayed
        // to the client exactly as it came back, same as every other
        // route in ProxyEndpoints.
        await ProxyEndpoints.WriteResponseAsync(httpContext, response, cancellationToken);
    }

    internal static async Task<string?> TryReadOrderIdAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        try
        {
            var payload = await response.Content.ReadFromJsonAsync<JsonElement>(cancellationToken);
            return payload.TryGetProperty("id", out var idProperty) ? idProperty.GetString() : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
