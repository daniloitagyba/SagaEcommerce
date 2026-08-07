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

    internal sealed record CheckoutRequest(string? CartId, string? CustomerId, string? CouponCode);

    internal sealed record CartSnapshot(IReadOnlyList<CartSnapshotItem> Items);

    internal sealed record CartSnapshotItem(string Sku, int Quantity);

    internal sealed record CheckoutOrderRequest(string CustomerId, IReadOnlyList<CheckoutOrderItem> Items, string? CouponCode);

    internal sealed record CheckoutOrderItem(string Sku, int Quantity);

    /// <summary>
    /// Cart.Service and Orders.Api know nothing of each other; this turns
    /// "what's in this shopper's cart" into Orders.Api's checkout call,
    /// injecting the Keycloak token here so the browser never needs to know
    /// Orders.Api requires auth. The cart is cleared only after Orders.Api
    /// accepts the order - clearing it first and having the order call
    /// then fail would strand the shopper with an empty cart and nothing
    /// purchased.
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
            // Buffered so the order id can be read for the log line below
            // and the body still relayed to the client afterwards - a
            // stream can only be read once otherwise.
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
                // The order already succeeded - a failure to clear the cart
                // afterwards must never read back as a checkout failure.
                StorefrontLog.CartClearFailedAfterCheckout(logger, request.CartId, orderId ?? "unknown", exception);
            }
        }

        // Relayed to the client exactly as Orders.Api answered, same as
        // every route in ProxyEndpoints.
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
