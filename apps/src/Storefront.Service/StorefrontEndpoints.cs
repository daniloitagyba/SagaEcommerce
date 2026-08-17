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

/// <summary>The BFF fan-out endpoints; every other Storefront.Service endpoint is a 1:1 reverse proxy.</summary>
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

    /// <summary>Fires the primary request, then a hedge after <paramref name="hedgeDelayMs"/>, and takes whichever finishes first; the loser is cancelled.</summary>
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

    /// <summary>Mirrors Orders.Api's ShippingAddressRequest shape.</summary>
    internal sealed record CheckoutShippingAddress(string? Line1, string? City, string? Region, string? PostalCode);

    internal sealed record CheckoutRequest(
        string? CouponCode,
        /// <summary>Card, Pix, or Boleto; null defaults to Pix.</summary>
        string? PaymentMethod = null,
        CheckoutShippingAddress? ShippingAddress = null);

    internal sealed record CartSnapshot(string CartId, IReadOnlyList<CartSnapshotItem> Items, long Version);

    /// <summary>Carries UnitPrice so this layer can assert an ExpectedSubtotal without a second Catalog round trip.</summary>
    internal sealed record CartSnapshotItem(string Sku, int Quantity, decimal UnitPrice);

    internal sealed record CheckoutOrderRequest(
        IReadOnlyList<CheckoutOrderItem> Items,
        string? CouponCode,
        string? PaymentMethod,
        CheckoutShippingAddress? ShippingAddress,
        decimal ExpectedSubtotal);

    internal sealed record CheckoutOrderItem(string Sku, int Quantity);

    /// <summary>Turns a shopper's cart into an Orders.Api order, forwarding the shopper's own bearer token and clearing the cart only after the order is accepted.</summary>
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

        var cartClient = httpClientFactory.CreateClient("cart");
        CartSnapshot? cart;
        try
        {
            using var cartRequest = new HttpRequestMessage(HttpMethod.Get, "/carts/me");
            ProxyEndpoints.CopyForwardedRequestHeaders(httpContext.Request, cartRequest);
            using var cartResponse = await cartClient.SendAsync(cartRequest, cancellationToken);
            if (cartResponse.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            {
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
                    StorefrontLog.CartClearFailedAfterCheckout(logger, "me", orderId ?? "unknown", exception);
                }
            }

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
        ProxyEndpoints.CopyForwardedRequestHeaders(incomingRequest, upstreamRequest, excludeHeaderName: "Idempotency-Key");

        var idempotencyKey = StorefrontCheckoutPolicy.BuildIdempotencyKey(authorization, cart);
        if (idempotencyKey is not null)
        {
            upstreamRequest.Headers.TryAddWithoutValidation("Idempotency-Key", idempotencyKey);
        }

        return await ordersClient.SendAsync(upstreamRequest, cancellationToken);
    }

    /// <summary>Detects Orders.Api's Price Changed 409, distinct from other Conflict causes, buffering the response for re-reading.</summary>
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

    /// <summary>Refreshes every cart line's price then re-reads the cart; returns null on any failure.</summary>
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
