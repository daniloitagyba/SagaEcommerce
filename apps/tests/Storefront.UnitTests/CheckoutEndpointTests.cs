using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Storefront.Service;

namespace Storefront.UnitTests;

/// <summary>
/// StorefrontEndpoints.CheckoutAsync turns "what's in this
/// cart" into an order - the one place here where getting the sequencing
/// wrong has a real consequence (clear the cart too early and the shopper
/// loses it for nothing; report a clear failure as a checkout failure and
/// a charged shopper sees an error). Exercised directly, not through HTTP,
/// since CheckoutAsync takes its collaborators as parameters.
///
/// The shopper's own bearer token is now the thing that
/// identifies them, forwarded to Orders.Api rather than reconstructed from
/// a service-account token plus a body-supplied customerId - so every test
/// here supplies an inbound Authorization header instead of a customerId.
///
/// Cart.Service resolves "/carts/me" from that same
/// forwarded token, so cartId is gone from the request entirely - every
/// test that used to pass one now just omits it.
/// </summary>
public sealed class CheckoutEndpointTests
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    [Fact]
    public async Task HappyPathPricesTheCartPostsTheOrderAndClearsTheCart()
    {
        var cartHandler = new RecordingHandler(request => request.Method == HttpMethod.Get
            ? JsonResponse(HttpStatusCode.OK, new { cartId = "cart-generation-7", items = new[] { new { sku = "SKU-BOOK-001", quantity = 2, unitPrice = 45m } }, version = 7 })
            : new HttpResponseMessage(HttpStatusCode.NoContent));
        var ordersHandler = new RecordingHandler(_ => JsonResponse(HttpStatusCode.Created, new { id = "order-1", status = "Created" }));

        var httpContext = await InvokeAsync(
            cartHandler, ordersHandler, couponCode: "SAVE10",
            paymentMethod: "Card", shippingAddress: new StorefrontEndpoints.CheckoutShippingAddress("Rua A, 1", "São Paulo", "SP", "01001-000"));

        Assert.Equal(StatusCodes.Status201Created, httpContext.Response.StatusCode);
        Assert.Contains("order-1", ReadBody(httpContext));

        var expectedAuthorization = $"Bearer {FakeToken("customer-1")}";
        var orderRequest = Assert.Single(ordersHandler.Requests);
        // The inbound shopper token, forwarded verbatim - not a token this layer minted.
        Assert.Equal(expectedAuthorization, orderRequest.AuthorizationHeader);
        // Deterministic from subject + cart generation + version.
        Assert.Equal("checkout:customer-1:cart-generation-7:7", orderRequest.IdempotencyKeyHeader);
        var orderBody = JsonSerializer.Deserialize<JsonElement>(orderRequest.Body!, JsonOptions);
        // No customerId in the body at all any more - Orders.Api derives it from the forwarded token's own claims.
        Assert.False(orderBody.TryGetProperty("customerId", out _));
        Assert.Equal("SAVE10", orderBody.GetProperty("couponCode").GetString());
        Assert.Equal("Card", orderBody.GetProperty("paymentMethod").GetString());
        Assert.Equal("São Paulo", orderBody.GetProperty("shippingAddress").GetProperty("city").GetString());
        // 2 x 45.00 - what the cart last saw the price as, for Orders.Api to compare against the live catalog.
        Assert.Equal(90m, orderBody.GetProperty("expectedSubtotal").GetDecimal());
        var items = orderBody.GetProperty("items").EnumerateArray().ToList();
        var item = Assert.Single(items);
        Assert.Equal("SKU-BOOK-001", item.GetProperty("sku").GetString());
        Assert.Equal(2, item.GetProperty("quantity").GetInt32());

        Assert.Equal(2, cartHandler.Requests.Count);
        Assert.Equal(HttpMethod.Get, cartHandler.Requests[0].Method);
        Assert.Equal(HttpMethod.Delete, cartHandler.Requests[1].Method);
        Assert.Equal("/carts/me?cartId=cart-generation-7&expectedVersion=7", cartHandler.Requests[1].PathAndQuery);
        // Cart.Service needs the same forwarded token to resolve "/carts/me" - both calls carry it.
        Assert.All(cartHandler.Requests, r => Assert.Equal(expectedAuthorization, r.AuthorizationHeader));
    }

    [Fact]
    public async Task NoAuthorizationHeaderIs401WithoutCallingAnyDependency()
    {
        var cartHandler = new RecordingHandler(_ => throw new InvalidOperationException("must not be called"));
        var ordersHandler = new RecordingHandler(_ => throw new InvalidOperationException("must not be called"));

        var httpContext = await InvokeAsync(cartHandler, ordersHandler, authorizationHeader: null);

        Assert.Equal(StatusCodes.Status401Unauthorized, httpContext.Response.StatusCode);
        Assert.Empty(cartHandler.Requests);
        Assert.Empty(ordersHandler.Requests);
    }

    [Fact]
    public async Task AnEmptyCartFailsValidationAndNeverCallsOrders()
    {
        var cartHandler = new RecordingHandler(_ => JsonResponse(HttpStatusCode.OK, new { items = Array.Empty<object>() }));
        var ordersHandler = new RecordingHandler(_ => throw new InvalidOperationException("must not be called"));

        var httpContext = await InvokeAsync(cartHandler, ordersHandler);

        Assert.Equal(StatusCodes.Status400BadRequest, httpContext.Response.StatusCode);
        Assert.Empty(ordersHandler.Requests);
    }

    [Fact]
    public async Task ACartServiceOutageReturns503WithoutCallingOrders()
    {
        var cartHandler = new RecordingHandler(_ => throw new HttpRequestException("connection refused"));
        var ordersHandler = new RecordingHandler(_ => throw new InvalidOperationException("must not be called"));

        var httpContext = await InvokeAsync(cartHandler, ordersHandler);

        Assert.Equal(StatusCodes.Status503ServiceUnavailable, httpContext.Response.StatusCode);
        Assert.Empty(ordersHandler.Requests);
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.Forbidden)]
    public async Task CartAuthenticationFailuresAreRelayedInsteadOfReportedAsAnOutage(HttpStatusCode statusCode)
    {
        var cartHandler = new RecordingHandler(_ => JsonResponse(statusCode, new { error = "auth rejected" }));
        var ordersHandler = new RecordingHandler(_ => throw new InvalidOperationException("must not be called"));

        var httpContext = await InvokeAsync(cartHandler, ordersHandler);

        Assert.Equal((int)statusCode, httpContext.Response.StatusCode);
        Assert.Contains("auth rejected", ReadBody(httpContext));
        Assert.Empty(ordersHandler.Requests);
    }

    [Fact]
    public async Task ARejectedOrderIsRelayedAsIsAndTheCartIsNotCleared()
    {
        // Orders.Api declines - the cart must survive so the shopper can retry without re-adding everything.
        var cartHandler = new RecordingHandler(_ => JsonResponse(
            HttpStatusCode.OK, new { items = new[] { new { sku = "SKU-UNKNOWN", quantity = 1 } } }));
        var ordersHandler = new RecordingHandler(_ => JsonResponse(
            HttpStatusCode.BadRequest, new { errors = new { items = (string[])["SKU 'SKU-UNKNOWN' was not found in the catalog."] } }));

        var httpContext = await InvokeAsync(cartHandler, ordersHandler);

        Assert.Equal(StatusCodes.Status400BadRequest, httpContext.Response.StatusCode);
        Assert.Contains("SKU-UNKNOWN", ReadBody(httpContext));
        // Only the GET happened - no DELETE.
        Assert.Single(cartHandler.Requests);
    }

    [Fact]
    public async Task ACartClearFailureAfterASuccessfulOrderIsNotReportedAsACheckoutFailure()
    {
        // The order is real and already accepted by the time the cart fails to clear, so the shopper must still see success.
        var cartHandler = new RecordingHandler(request => request.Method == HttpMethod.Get
            ? JsonResponse(HttpStatusCode.OK, new { cartId = "cart-generation-2", items = new[] { new { sku = "SKU-BOOK-001", quantity = 1 } }, version = 2 })
            : throw new HttpRequestException("connection reset"));
        var ordersHandler = new RecordingHandler(_ => JsonResponse(HttpStatusCode.Created, new { id = "order-2", status = "Created" }));

        var httpContext = await InvokeAsync(cartHandler, ordersHandler);

        Assert.Equal(StatusCodes.Status201Created, httpContext.Response.StatusCode);
        Assert.Contains("order-2", ReadBody(httpContext));
    }

    /// <summary>
    /// The fix for the audit's finding 10: a moved catalog price used to
    /// have no resolution short of the shopper removing and re-adding
    /// every affected item. Orders.Api's 409 "Price Changed" now triggers
    /// one automatic reprice-and-retry entirely inside this layer.
    /// </summary>
    [Fact]
    public async Task APriceMismatchRepricesTheCartAndRetriesCheckoutOnce()
    {
        var cartGetCount = 0;
        var cartHandler = new RecordingHandler(request =>
        {
            if (request.Method == HttpMethod.Get)
            {
                cartGetCount++;
                // The second GET, after the reprice below, reflects the refreshed price/version.
                var (unitPrice, version) = cartGetCount == 1 ? (45m, 7L) : (50m, 8L);
                return JsonResponse(HttpStatusCode.OK, new
                {
                    cartId = "cart-generation-7",
                    items = new[] { new { sku = "SKU-BOOK-001", quantity = 2, unitPrice } },
                    version
                });
            }

            if (request.Method == HttpMethod.Post)
            {
                return new HttpResponseMessage(HttpStatusCode.OK);
            }

            return new HttpResponseMessage(HttpStatusCode.NoContent);
        });

        var ordersCallCount = 0;
        var ordersHandler = new RecordingHandler(_ =>
        {
            ordersCallCount++;
            return ordersCallCount == 1
                ? JsonResponse(HttpStatusCode.Conflict, new { title = "Price Changed", detail = "prices moved", expectedSubtotal = 90m, actualSubtotal = 100m })
                : JsonResponse(HttpStatusCode.Created, new { id = "order-repriced", status = "Created" });
        });

        var httpContext = await InvokeAsync(cartHandler, ordersHandler);

        Assert.Equal(StatusCodes.Status201Created, httpContext.Response.StatusCode);
        Assert.Contains("order-repriced", ReadBody(httpContext));
        Assert.Equal(2, ordersHandler.Requests.Count);

        // The retry priced against the refreshed 50 unit price (2 x 50 = 100), not the stale 45.
        var retryBody = JsonSerializer.Deserialize<JsonElement>(ordersHandler.Requests[1].Body!, JsonOptions);
        Assert.Equal(100m, retryBody.GetProperty("expectedSubtotal").GetDecimal());
        // A different Idempotency-Key from the first attempt - the cart's version moved when it was repriced.
        Assert.NotEqual(ordersHandler.Requests[0].IdempotencyKeyHeader, ordersHandler.Requests[1].IdempotencyKeyHeader);

        Assert.Contains(cartHandler.Requests, r => r.Method == HttpMethod.Post && r.PathAndQuery.Contains("refresh-price", StringComparison.Ordinal));
        Assert.Equal(HttpMethod.Delete, cartHandler.Requests[^1].Method);
    }

    [Fact]
    public async Task APriceMismatchThatCannotBeRepricedRelaysTheOriginalConflict()
    {
        var cartHandler = new RecordingHandler(request =>
        {
            if (request.Method == HttpMethod.Get)
            {
                return JsonResponse(HttpStatusCode.OK, new
                {
                    cartId = "cart-generation-7",
                    items = new[] { new { sku = "SKU-VANISHED", quantity = 1, unitPrice = 45m } },
                    version = 7
                });
            }

            if (request.Method == HttpMethod.Post)
            {
                // The SKU no longer resolves in the catalog at all - nothing to reprice against.
                return new HttpResponseMessage(HttpStatusCode.NotFound);
            }

            throw new InvalidOperationException("must not be called - a failed reprice must not clear the cart");
        });

        var ordersHandler = new RecordingHandler(_ => JsonResponse(
            HttpStatusCode.Conflict, new { title = "Price Changed", detail = "prices moved", expectedSubtotal = 45m, actualSubtotal = 50m }));

        var httpContext = await InvokeAsync(cartHandler, ordersHandler);

        Assert.Equal(StatusCodes.Status409Conflict, httpContext.Response.StatusCode);
        Assert.Contains("Price Changed", ReadBody(httpContext));
        // Only the original attempt - a reprice that could not complete must not retry against a still-stale cart.
        Assert.Single(ordersHandler.Requests);
    }

    [Fact]
    public async Task ADifferentConflictIsNotTreatedAsAPriceMismatch()
    {
        var cartHandler = new RecordingHandler(request => request.Method == HttpMethod.Get
            ? JsonResponse(HttpStatusCode.OK, new { cartId = "cart-generation-1", items = new[] { new { sku = "SKU-BOOK-001", quantity = 1, unitPrice = 45m } }, version = 1 })
            : throw new InvalidOperationException("must not be called - only a Price Changed 409 triggers a reprice"));

        var ordersHandler = new RecordingHandler(_ => JsonResponse(
            HttpStatusCode.Conflict, new { title = "Idempotency Key Conflict", detail = "reused" }));

        var httpContext = await InvokeAsync(cartHandler, ordersHandler);

        Assert.Equal(StatusCodes.Status409Conflict, httpContext.Response.StatusCode);
        Assert.Contains("Idempotency Key Conflict", ReadBody(httpContext));
        Assert.Single(ordersHandler.Requests);
        // Only the one GET that priced the original attempt - no refresh-price, no retry, no clear.
        Assert.Single(cartHandler.Requests);
    }

    private static async Task<DefaultHttpContext> InvokeAsync(
        RecordingHandler cartHandler,
        RecordingHandler ordersHandler,
        string? couponCode = null,
        string? paymentMethod = null,
        StorefrontEndpoints.CheckoutShippingAddress? shippingAddress = null,
        string? authorizationHeader = "default")
    {
        var httpContext = new DefaultHttpContext { Response = { Body = new MemoryStream() } };
        // Results.Problem resolves IProblemDetailsService from RequestServices while formatting - it just can't be null, which DefaultHttpContext leaves it as.
        httpContext.RequestServices = new ServiceCollection().AddLogging().BuildServiceProvider();
        var resolvedHeader = authorizationHeader switch
        {
            "default" => $"Bearer {FakeToken("customer-1")}",
            null => null,
            _ => authorizationHeader
        };
        if (resolvedHeader is not null)
        {
            httpContext.Request.Headers.Authorization = resolvedHeader;
        }

        var httpClientFactory = new FakeHttpClientFactory(new Dictionary<string, HttpMessageHandler>(StringComparer.Ordinal)
        {
            ["cart"] = cartHandler,
            ["orders"] = ordersHandler
        });
        var request = new StorefrontEndpoints.CheckoutRequest(couponCode, paymentMethod, shippingAddress);

        await StorefrontEndpoints.CheckoutAsync(
            request,
            httpContext,
            httpClientFactory,
            NullLoggerFactory.Instance,
            CancellationToken.None);

        httpContext.Response.Body.Position = 0;
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

    private static string ReadBody(HttpContext httpContext)
    {
        httpContext.Response.Body.Position = 0;
        using var reader = new StreamReader(httpContext.Response.Body);
        return reader.ReadToEnd();
    }

    private static HttpResponseMessage JsonResponse(HttpStatusCode statusCode, object body) =>
        new(statusCode)
        {
            Content = new StringContent(JsonSerializer.Serialize(body, JsonOptions), Encoding.UTF8, "application/json")
        };

    private sealed class FakeHttpClientFactory(IReadOnlyDictionary<string, HttpMessageHandler> handlersByName) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) =>
            new(handlersByName[name]) { BaseAddress = new Uri($"http://{name}.invalid") };
    }

    private sealed record RecordedRequest(
        HttpMethod Method,
        string PathAndQuery,
        string? Body,
        string? AuthorizationHeader,
        string? IdempotencyKeyHeader);

    /// <summary>Snapshots method/body/auth-header at Send time, not the HttpRequestMessage itself - CheckoutAsync disposes it afterward.</summary>
    private sealed class RecordingHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public List<RecordedRequest> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
            var idempotencyKey = request.Headers.TryGetValues("Idempotency-Key", out var values) ? values.FirstOrDefault() : null;
            Requests.Add(new RecordedRequest(
                request.Method,
                request.RequestUri?.PathAndQuery ?? string.Empty,
                body,
                request.Headers.Authorization?.ToString(),
                idempotencyKey));
            return respond(request);
        }
    }
}
