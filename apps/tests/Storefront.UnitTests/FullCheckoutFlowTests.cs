using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Storefront.Service;

namespace Storefront.UnitTests;

/// <summary>
/// Walks the same journey a shopper's browser drives through
/// Storefront.Service's BFF: browse a product, add it to the cart, check
/// out, then look up the order and its history - the exact sequence that
/// surfaced the "place order didn't work" bug (OrdersProxyEndpointTests
/// covers that specific bug in isolation; this covers the sequence as a
/// whole, one endpoint method call at a time, the same way a browser would
/// call them one HTTP request at a time). Each step uses its own
/// RecordingHandler rather than a shared fake backend - this is verifying
/// Storefront.Service's own composition and forwarding logic (what URL,
/// what method, whose bearer token), not re-implementing Catalog/Cart/
/// Orders' own behavior.
/// </summary>
public sealed class FullCheckoutFlowTests
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private const string Sku = "SKU-ELEC-001";

    [Fact]
    public async Task ShopperCanBrowseAddToCartCheckoutAndThenViewTheOrderAndItsHistory()
    {
        var authorization = $"Bearer {FakeToken("customer-42")}";

        // 1. Browse: GET /api/storefront/products/{sku} - Catalog + Inventory fan-out.
        var catalogHandler = new RecordingHandler(_ => JsonResponse(HttpStatusCode.OK,
            new { sku = Sku, name = "Notebook Ultraslim 14\"", price = 4299.90m, currency = "BRL" }));
        var inventoryHandler = new RecordingHandler(_ => JsonResponse(HttpStatusCode.OK,
            new { sku = Sku, availability = "InStock" }));

        var productResult = await StorefrontEndpoints.GetProductSummaryAsync(
            Sku,
            new FakeHttpClientFactory(new Dictionary<string, HttpMessageHandler>(StringComparer.Ordinal)
            {
                ["catalog"] = catalogHandler,
                ["inventory"] = inventoryHandler
            }),
            Options.Create(new ProductSummaryOptions { HedgeDelayMilliseconds = 0 }),
            NullLoggerFactory.Instance,
            CancellationToken.None);

        var productHttpContext = NewHttpContext();
        await productResult.ExecuteAsync(productHttpContext);
        Assert.Equal(StatusCodes.Status200OK, productHttpContext.Response.StatusCode);
        var catalogRequest = Assert.Single(catalogHandler.Requests);
        Assert.Equal($"/products/by-sku/{Sku}", catalogRequest.RequestUri!.AbsolutePath);

        // 2. Add to cart: PUT /api/cart/carts/me/items/{sku}.
        var cartPutHandler = new RecordingHandler(_ => new HttpResponseMessage(HttpStatusCode.NoContent));
        var cartPutContext = NewHttpContext(method: HttpMethods.Put, authorization: authorization, body: """{"quantity":1}""");
        await ProxyEndpoints.ForwardAsync(
            new FakeHttpClientFactory(new Dictionary<string, HttpMessageHandler>(StringComparer.Ordinal) { ["cart"] = cartPutHandler }).CreateClient("cart"),
            $"carts/me/items/{Sku}",
            cartPutContext.Request,
            CancellationToken.None);
        Assert.Equal(StatusCodes.Status204NoContent, cartPutContext.Response.StatusCode);
        var cartPutRequest = Assert.Single(cartPutHandler.Requests);
        Assert.Equal(HttpMethod.Put, cartPutRequest.Method);
        Assert.Equal($"/carts/me/items/{Sku}", cartPutRequest.RequestUri!.AbsolutePath);
        Assert.Equal(authorization, cartPutRequest.AuthorizationHeader);

        // 3. Checkout: POST /api/storefront/checkout - reads the cart, prices it, creates the order, clears the cart.
        const string orderId = "a6ea5218-7c89-450b-bcf6-1fdae2dc3827";
        var cartHandler = new RecordingHandler(request => request.Method == HttpMethod.Get
            ? JsonResponse(HttpStatusCode.OK, new { items = new[] { new { sku = Sku, quantity = 1, unitPrice = 4299.90m } }, version = 1 })
            : new HttpResponseMessage(HttpStatusCode.NoContent));
        var ordersCreateHandler = new RecordingHandler(_ => JsonResponse(HttpStatusCode.Created, new { id = orderId, status = "Created" }));

        var checkoutContext = NewHttpContext(method: HttpMethods.Post, authorization: authorization);
        await StorefrontEndpoints.CheckoutAsync(
            new StorefrontEndpoints.CheckoutRequest(CouponCode: null, PaymentMethod: "Pix", ShippingAddress: null),
            checkoutContext,
            new FakeHttpClientFactory(new Dictionary<string, HttpMessageHandler>(StringComparer.Ordinal)
            {
                ["cart"] = cartHandler,
                ["orders"] = ordersCreateHandler
            }),
            NullLoggerFactory.Instance,
            CancellationToken.None);

        Assert.Equal(StatusCodes.Status201Created, checkoutContext.Response.StatusCode);
        checkoutContext.Response.Body.Position = 0;
        Assert.Contains(orderId, await new StreamReader(checkoutContext.Response.Body).ReadToEndAsync());
        // The cart was cleared after the order was accepted - a shopper reloading the cart afterward sees it empty.
        Assert.Contains(cartHandler.Requests, r => r.Method == HttpMethod.Delete);

        // 4. View the order: GET /api/orders/{id} - this is the exact request that 404d before the "orders/" prefix fix.
        var orderGetHandler = new RecordingHandler(_ => JsonResponse(HttpStatusCode.OK, new { id = orderId, status = "Created" }));
        var orderGetContext = NewHttpContext(authorization: authorization);
        await ProxyEndpoints.ForwardOrdersSubPathAsync(
            orderId,
            orderGetContext.Request,
            new FakeHttpClientFactory(orderGetHandler),
            CancellationToken.None);
        Assert.Equal(StatusCodes.Status200OK, orderGetContext.Response.StatusCode);
        Assert.Equal($"/orders/{orderId}", Assert.Single(orderGetHandler.Requests).RequestUri!.AbsolutePath);

        // 5. View its history: GET /api/orders/{id}/history - same bug, same fix.
        var orderHistoryHandler = new RecordingHandler(_ => JsonResponse(HttpStatusCode.OK, new { entries = Array.Empty<object>() }));
        var orderHistoryContext = NewHttpContext(authorization: authorization);
        await ProxyEndpoints.ForwardOrdersSubPathAsync(
            $"{orderId}/history",
            orderHistoryContext.Request,
            new FakeHttpClientFactory(orderHistoryHandler),
            CancellationToken.None);
        Assert.Equal(StatusCodes.Status200OK, orderHistoryContext.Response.StatusCode);
        Assert.Equal($"/orders/{orderId}/history", Assert.Single(orderHistoryHandler.Requests).RequestUri!.AbsolutePath);
    }

    private static DefaultHttpContext NewHttpContext(string method = "GET", string? authorization = null, string? body = null)
    {
        var httpContext = new DefaultHttpContext { Response = { Body = new MemoryStream() } };
        httpContext.RequestServices = new ServiceCollection().AddLogging().BuildServiceProvider();
        httpContext.Request.Method = method;
        if (authorization is not null)
        {
            httpContext.Request.Headers.Authorization = authorization;
        }

        if (body is not null)
        {
            var bytes = Encoding.UTF8.GetBytes(body);
            httpContext.Request.Body = new MemoryStream(bytes);
            httpContext.Request.ContentLength = bytes.Length;
        }

        return httpContext;
    }

    /// <summary>An unsigned, structurally-valid JWT - enough for UnverifiedJwt to read a claim back out, which is all this layer ever does with it.</summary>
    private static string FakeToken(string subject)
    {
        var payload = JsonSerializer.Serialize(new { preferred_username = subject, sub = subject });
        var payloadSegment = Convert.ToBase64String(Encoding.UTF8.GetBytes(payload))
            .TrimEnd('=').Replace('+', '-').Replace('/', '_');
        return $"header.{payloadSegment}.signature";
    }

    private static HttpResponseMessage JsonResponse(HttpStatusCode statusCode, object body) =>
        new(statusCode)
        {
            Content = new StringContent(JsonSerializer.Serialize(body, JsonOptions), Encoding.UTF8, "application/json")
        };

    private sealed class FakeHttpClientFactory : IHttpClientFactory
    {
        private readonly IReadOnlyDictionary<string, HttpMessageHandler> _handlersByName;

        public FakeHttpClientFactory(IReadOnlyDictionary<string, HttpMessageHandler> handlersByName) => _handlersByName = handlersByName;

        public FakeHttpClientFactory(HttpMessageHandler handler) =>
            _handlersByName = new Dictionary<string, HttpMessageHandler>(StringComparer.Ordinal) { ["orders"] = handler };

        public HttpClient CreateClient(string name) =>
            new(_handlersByName[name]) { BaseAddress = new Uri($"http://{name}.invalid") };
    }

    private sealed record RecordedRequest(HttpMethod Method, Uri? RequestUri, string? AuthorizationHeader);

    /// <summary>Snapshots method/URI/auth-header at Send time - the real code disposes the HttpRequestMessage afterward.</summary>
    private sealed class RecordingHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public List<RecordedRequest> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(new RecordedRequest(request.Method, request.RequestUri, request.Headers.Authorization?.ToString()));
            return Task.FromResult(respond(request));
        }
    }
}
