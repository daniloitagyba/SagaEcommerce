using System.Net;
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
/// This lab's genuine BFF fan-out - every other Storefront.Service
/// endpoint (ProxyEndpoints) is a 1:1 reverse proxy. GetProductSummaryAsync
/// calls Catalog and Inventory in parallel (see ProductSummaryOptions for
/// why Inventory is hedged); CheckoutAsync turns a cart into an order.
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

    internal static async Task<IResult> GetProductSummaryAsync(
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

        // Awaited separately, not via Task.WhenAll: Inventory only enriches
        // an otherwise-complete Catalog response, and WhenAll would fail
        // the whole request if Inventory faults even though Catalog (which
        // determines whether the SKU exists) answered fine.
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
    /// <paramref name="hedgeDelayMs"/>, fires a second one at the same
    /// backend (the K8s Service load-balances new connections, so it has a
    /// real chance of landing on a different pod) and takes whichever
    /// finishes first. The loser is cancelled rather than left running -
    /// measured to matter, since uncancelled losers pile up and contend for
    /// the same connection pool. Safe here because it's a read-only GET.
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

    /// <summary>Mirrors Orders.Api's ShippingAddressRequest shape - Storefront has no reference to that assembly to reuse it directly.</summary>
    internal sealed record CheckoutShippingAddress(string? Line1, string? City, string? Region, string? PostalCode);

    internal sealed record CheckoutRequest(
        string? CouponCode,
        /// <summary>Card, Pix, or Boleto. Null defaults to Pix, same as Orders.Api.</summary>
        string? PaymentMethod = null,
        CheckoutShippingAddress? ShippingAddress = null);

    internal sealed record CartSnapshot(string CartId, IReadOnlyList<CartSnapshotItem> Items, long Version);

    /// <summary>UnitPrice travels with the snapshot so this layer can assert an ExpectedSubtotal without a second Catalog round trip.</summary>
    internal sealed record CartSnapshotItem(string Sku, int Quantity, decimal UnitPrice);

    internal sealed record CheckoutOrderRequest(
        IReadOnlyList<CheckoutOrderItem> Items,
        string? CouponCode,
        string? PaymentMethod,
        CheckoutShippingAddress? ShippingAddress,
        decimal ExpectedSubtotal);

    internal sealed record CheckoutOrderItem(string Sku, int Quantity);

    /// <summary>
    /// Cart.Service and Orders.Api know nothing of each other; this turns
    /// "what's in this shopper's cart" into Orders.Api's checkout call.
    ///
    /// Forwards the shopper's own bearer token to Orders.Api
    /// rather than minting a service-account one - replacing a design
    /// where Storefront authenticated as itself and simply asserted
    /// whatever customerId the request body carried, which any caller
    /// could set to anyone. Orders.Api now derives the order's customerId
    /// from that same forwarded token's claims, so there is nothing left
    /// for this layer to assert on the shopper's behalf. One fewer trusted
    /// intermediary: Orders.Api validates the shopper's own token directly,
    /// the same JWKS-backed check every other caller goes through, not a
    /// second-hand claim Storefront re-signs.
    ///
    /// The cart is cleared only after Orders.Api accepts the order -
    /// clearing it first and having the order call then fail would strand
    /// the shopper with an empty cart and nothing purchased.
    /// </summary>
    internal static async Task CheckoutAsync(
        CheckoutRequest request,
        HttpContext httpContext,
        IHttpClientFactory httpClientFactory,
        ILoggerFactory loggerFactory,
        CancellationToken cancellationToken)
    {
        var logger = loggerFactory.CreateLogger("Storefront.Service.StorefrontEndpoints");

        var authorization = httpContext.Request.Headers.Authorization.FirstOrDefault();
        if (string.IsNullOrWhiteSpace(authorization))
        {
            await Results.Problem(
                detail: "Checkout requires a signed-in shopper.",
                statusCode: StatusCodes.Status401Unauthorized).ExecuteAsync(httpContext);
            return;
        }

        // Cart.Service resolves "/carts/me" from this same
        // forwarded token - there is no cartId left to pass, the cart IS
        // the shopper's, the same way the order about to be created is.
        var cartClient = httpClientFactory.CreateClient("cart");
        CartSnapshot? cart;
        try
        {
            using var cartRequest = new HttpRequestMessage(HttpMethod.Get, "/carts/me");
            ProxyEndpoints.CopyForwardedRequestHeaders(httpContext.Request, cartRequest);
            using var cartResponse = await cartClient.SendAsync(cartRequest, cancellationToken);
            if (cartResponse.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            {
                // Authentication/authorization is a caller outcome, not a
                // dependency outage. Preserve the upstream status and
                // challenge headers instead of converting it into 503.
                await ProxyEndpoints.WriteResponseAsync(httpContext, cartResponse, cancellationToken);
                return;
            }

            cartResponse.EnsureSuccessStatusCode();
            cart = await cartResponse.Content.ReadFromJsonAsync<CartSnapshot>(JsonOptions, cancellationToken);
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException)
        {
            StorefrontLog.CartUnavailableDuringCheckout(logger, "me", exception);
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

        var orderRequest = StorefrontCheckoutPolicy.BuildOrderRequest(cart, request);
        var ordersClient = httpClientFactory.CreateClient("orders");

        var response = await PostOrderAsync(ordersClient, orderRequest, httpContext.Request, authorization, cart, cancellationToken);

        // A moved catalog price is not the shopper's fault, and until now
        // had no resolution short of removing and re-adding every affected
        // item. One automatic reprice-and-retry, entirely server-side: the
        // frontend that called this endpoint sees either the success it
        // originally asked for, or - if the reprice itself fails, or prices
        // moved again in the meantime - the exact same Price Changed 409 it
        // already knows how to show, never a new failure mode.
        if (await IsPriceMismatchAsync(response, cancellationToken))
        {
            var repricedCart = await TryRepriceCartAsync(cartClient, httpContext.Request, cart, logger, cancellationToken);
            if (repricedCart is not null)
            {
                response.Dispose();
                cart = repricedCart;
                var retryOrderRequest = StorefrontCheckoutPolicy.BuildOrderRequest(cart, request);
                response = await PostOrderAsync(ordersClient, retryOrderRequest, httpContext.Request, authorization, cart, cancellationToken);
            }
        }

        using (response)
        {
            if (response.IsSuccessStatusCode)
            {
                // Buffered so the order id can be read for the log line below
                // and the body still relayed to the client afterwards - a
                // stream can only be read once otherwise.
                await response.Content.LoadIntoBufferAsync(cancellationToken);
                var orderId = await TryReadOrderIdAsync(response, cancellationToken);

                try
                {
                    var clearPath = $"/carts/me?cartId={Uri.EscapeDataString(cart.CartId)}&expectedVersion={cart.Version}";
                    using var clearRequest = new HttpRequestMessage(HttpMethod.Delete, clearPath);
                    ProxyEndpoints.CopyForwardedRequestHeaders(httpContext.Request, clearRequest);
                    using var clearResponse = await cartClient.SendAsync(clearRequest, cancellationToken);
                    clearResponse.EnsureSuccessStatusCode();
                }
                catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException)
                {
                    // The order already succeeded - a failure to clear the cart
                    // afterwards must never read back as a checkout failure.
                    StorefrontLog.CartClearFailedAfterCheckout(logger, "me", orderId ?? "unknown", exception);
                }
            }

            // Relayed to the client exactly as Orders.Api answered, same as
            // every route in ProxyEndpoints.
            await ProxyEndpoints.WriteResponseAsync(httpContext, response, cancellationToken);
        }
    }

    private static async Task<HttpResponseMessage> PostOrderAsync(
        HttpClient ordersClient,
        CheckoutOrderRequest orderRequest,
        HttpRequest incomingRequest,
        string authorization,
        CartSnapshot cart,
        CancellationToken cancellationToken)
    {
        using var upstreamRequest = new HttpRequestMessage(HttpMethod.Post, "/orders")
        {
            Content = JsonContent.Create(orderRequest, options: JsonOptions)
        };
        // Idempotency-Key excluded: this endpoint always overrides it with
        // the deterministic key computed below, never the caller-supplied
        // one - see that key's own comment for why.
        ProxyEndpoints.CopyForwardedRequestHeaders(incomingRequest, upstreamRequest, excludeHeaderName: "Idempotency-Key");

        // Deterministic, not client-generated - this exact
        // cart state ("this shopper, this cart generation, this version") checks out at most
        // once. A double-submitted click carries the identical version and
        // replays instead of double-charging; adding or removing an item
        // bumps the version - including a repriced item, see
        // TryRepriceCartAsync - so a genuinely new checkout after the cart
        // changed is never blocked by a stale key. The subject comes from the
        // same forwarded token, read without verifying it - only
        // uniqueness per shopper matters here, and Orders.Api verifies the
        // token itself regardless of what this layer assumed about it.
        var idempotencyKey = StorefrontCheckoutPolicy.BuildIdempotencyKey(authorization, cart);
        if (idempotencyKey is not null)
        {
            upstreamRequest.Headers.TryAddWithoutValidation("Idempotency-Key", idempotencyKey);
        }

        return await ordersClient.SendAsync(upstreamRequest, cancellationToken);
    }

    /// <summary>
    /// Orders.Api's own distinguishing signal for this specific 409 - see
    /// OrderEndpoints' PriceMismatch mapping - not just any Conflict, which
    /// also covers a lost coupon slot and an idempotency-key reuse, neither
    /// of which a reprice-and-retry would ever resolve. Buffers the content
    /// so it can still be read again (by the retry decision here, and by
    /// WriteResponseAsync afterwards if no retry happens) - the same
    /// pattern the success branch below already uses for the same reason.
    /// </summary>
    private static async Task<bool> IsPriceMismatchAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        if (response.StatusCode != HttpStatusCode.Conflict)
        {
            return false;
        }

        await response.Content.LoadIntoBufferAsync(cancellationToken);
        try
        {
            var problem = await response.Content.ReadFromJsonAsync<JsonElement>(cancellationToken);
            return problem.TryGetProperty("title", out var title)
                && string.Equals(title.GetString(), "Price Changed", StringComparison.Ordinal);
        }
        catch (JsonException)
        {
            return false;
        }
    }

    /// <summary>
    /// Refreshes every cart line's price via Cart.Service's refresh-price
    /// endpoint (CartEndpoints.RefreshItemPriceAsync), then re-reads the
    /// cart so the retry above prices against what Orders.Api will actually
    /// charge this time. Null on any failure - Cart.Service unreachable, or
    /// a SKU no longer resolvable in the catalog at all - so the caller
    /// falls back to relaying the original Price Changed response rather
    /// than guessing at a cart this couldn't actually bring current.
    /// </summary>
    private static async Task<CartSnapshot?> TryRepriceCartAsync(
        HttpClient cartClient,
        HttpRequest incomingRequest,
        CartSnapshot cart,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        try
        {
            foreach (var item in cart.Items)
            {
                using var refreshRequest = new HttpRequestMessage(
                    HttpMethod.Post, $"/carts/me/items/{Uri.EscapeDataString(item.Sku)}/refresh-price");
                ProxyEndpoints.CopyForwardedRequestHeaders(incomingRequest, refreshRequest);
                using var refreshResponse = await cartClient.SendAsync(refreshRequest, cancellationToken);
                if (!refreshResponse.IsSuccessStatusCode)
                {
                    return null;
                }
            }

            using var cartRequest = new HttpRequestMessage(HttpMethod.Get, "/carts/me");
            ProxyEndpoints.CopyForwardedRequestHeaders(incomingRequest, cartRequest);
            using var cartResponse = await cartClient.SendAsync(cartRequest, cancellationToken);
            cartResponse.EnsureSuccessStatusCode();
            return await cartResponse.Content.ReadFromJsonAsync<CartSnapshot>(JsonOptions, cancellationToken);
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException)
        {
            StorefrontLog.CartUnavailableDuringCheckout(logger, "me", exception);
            return null;
        }
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
