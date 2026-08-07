using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Storefront.Service;

namespace Storefront.UnitTests;

/// <summary>
/// Milestone 66: StorefrontEndpoints.CheckoutAsync turns "what's in this
/// cart" into an order, and is the one place in this service where getting
/// the sequencing wrong has a real consequence - clear the cart before the
/// order is confirmed and a shopper loses their cart for nothing; report a
/// cart-clear failure as a checkout failure and a shopper who was actually
/// charged sees an error. Both are exercised directly here rather than
/// through the HTTP layer, since CheckoutAsync takes its collaborators
/// (IHttpClientFactory, KeycloakTokenProvider, ILoggerFactory) as
/// parameters and needs no ASP.NET Core host to invoke.
/// </summary>
public sealed class CheckoutEndpointTests
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    [Fact]
    public async Task HappyPathPricesTheCartPostsTheOrderAndClearsTheCart()
    {
        var cartHandler = new RecordingHandler(request => request.Method == HttpMethod.Get
            ? JsonResponse(HttpStatusCode.OK, new { items = new[] { new { sku = "SKU-BOOK-001", quantity = 2 } } })
            : new HttpResponseMessage(HttpStatusCode.NoContent));
        var ordersHandler = new RecordingHandler(_ => JsonResponse(HttpStatusCode.Created, new { id = "order-1", status = "Created" }));

        var httpContext = await InvokeAsync(cartHandler, ordersHandler, "cart-1", "customer-1", couponCode: "SAVE10");

        Assert.Equal(StatusCodes.Status201Created, httpContext.Response.StatusCode);
        Assert.Contains("order-1", ReadBody(httpContext));

        var orderRequest = Assert.Single(ordersHandler.Requests);
        Assert.Equal("Bearer test-token", orderRequest.AuthorizationHeader);
        var orderBody = JsonSerializer.Deserialize<JsonElement>(orderRequest.Body!, JsonOptions);
        Assert.Equal("customer-1", orderBody.GetProperty("customerId").GetString());
        Assert.Equal("SAVE10", orderBody.GetProperty("couponCode").GetString());
        var items = orderBody.GetProperty("items").EnumerateArray().ToList();
        var item = Assert.Single(items);
        Assert.Equal("SKU-BOOK-001", item.GetProperty("sku").GetString());
        Assert.Equal(2, item.GetProperty("quantity").GetInt32());

        Assert.Equal(2, cartHandler.Requests.Count);
        Assert.Equal(HttpMethod.Get, cartHandler.Requests[0].Method);
        Assert.Equal(HttpMethod.Delete, cartHandler.Requests[1].Method);
    }

    [Fact]
    public async Task MissingCartIdFailsValidationWithoutCallingAnyDependency()
    {
        var cartHandler = new RecordingHandler(_ => throw new InvalidOperationException("must not be called"));
        var ordersHandler = new RecordingHandler(_ => throw new InvalidOperationException("must not be called"));

        var httpContext = await InvokeAsync(cartHandler, ordersHandler, cartId: null, customerId: "customer-1");

        Assert.Equal(StatusCodes.Status400BadRequest, httpContext.Response.StatusCode);
        Assert.Empty(cartHandler.Requests);
        Assert.Empty(ordersHandler.Requests);
    }

    [Fact]
    public async Task AnEmptyCartFailsValidationAndNeverCallsOrders()
    {
        var cartHandler = new RecordingHandler(_ => JsonResponse(HttpStatusCode.OK, new { items = Array.Empty<object>() }));
        var ordersHandler = new RecordingHandler(_ => throw new InvalidOperationException("must not be called"));

        var httpContext = await InvokeAsync(cartHandler, ordersHandler, "cart-1", "customer-1");

        Assert.Equal(StatusCodes.Status400BadRequest, httpContext.Response.StatusCode);
        Assert.Empty(ordersHandler.Requests);
    }

    [Fact]
    public async Task ACartServiceOutageReturns503WithoutCallingOrders()
    {
        var cartHandler = new RecordingHandler(_ => throw new HttpRequestException("connection refused"));
        var ordersHandler = new RecordingHandler(_ => throw new InvalidOperationException("must not be called"));

        var httpContext = await InvokeAsync(cartHandler, ordersHandler, "cart-1", "customer-1");

        Assert.Equal(StatusCodes.Status503ServiceUnavailable, httpContext.Response.StatusCode);
        Assert.Empty(ordersHandler.Requests);
    }

    [Fact]
    public async Task ARejectedOrderIsRelayedAsIsAndTheCartIsNotCleared()
    {
        // Orders.Api declines - a bad SKU, a duplicate, whatever - and the
        // cart must survive exactly as it was so the shopper can fix the
        // request and retry without re-adding everything.
        var cartHandler = new RecordingHandler(_ => JsonResponse(
            HttpStatusCode.OK, new { items = new[] { new { sku = "SKU-UNKNOWN", quantity = 1 } } }));
        var ordersHandler = new RecordingHandler(_ => JsonResponse(
            HttpStatusCode.BadRequest, new { errors = new { items = (string[])["SKU 'SKU-UNKNOWN' was not found in the catalog."] } }));

        var httpContext = await InvokeAsync(cartHandler, ordersHandler, "cart-1", "customer-1");

        Assert.Equal(StatusCodes.Status400BadRequest, httpContext.Response.StatusCode);
        Assert.Contains("SKU-UNKNOWN", ReadBody(httpContext));
        // Only the GET happened - no DELETE.
        Assert.Single(cartHandler.Requests);
    }

    [Fact]
    public async Task ACartClearFailureAfterASuccessfulOrderIsNotReportedAsACheckoutFailure()
    {
        // The one sequencing invariant that matters most: the order is
        // real and already accepted by the time the cart fails to clear,
        // so the shopper must still see success - anything else makes
        // them think they were not charged for an order that exists.
        var cartHandler = new RecordingHandler(request => request.Method == HttpMethod.Get
            ? JsonResponse(HttpStatusCode.OK, new { items = new[] { new { sku = "SKU-BOOK-001", quantity = 1 } } })
            : throw new HttpRequestException("connection reset"));
        var ordersHandler = new RecordingHandler(_ => JsonResponse(HttpStatusCode.Created, new { id = "order-2", status = "Created" }));

        var httpContext = await InvokeAsync(cartHandler, ordersHandler, "cart-1", "customer-1");

        Assert.Equal(StatusCodes.Status201Created, httpContext.Response.StatusCode);
        Assert.Contains("order-2", ReadBody(httpContext));
    }

    private static async Task<DefaultHttpContext> InvokeAsync(
        RecordingHandler cartHandler,
        RecordingHandler ordersHandler,
        string? cartId,
        string? customerId,
        string? couponCode = null)
    {
        var httpContext = new DefaultHttpContext { Response = { Body = new MemoryStream() } };
        // Results.ValidationProblem/Results.Problem resolve IProblemDetailsService
        // from HttpContext.RequestServices while formatting the response
        // (falling back to their own default JSON if it isn't registered) -
        // it just can't be null, which DefaultHttpContext leaves it as.
        httpContext.RequestServices = new ServiceCollection().AddLogging().BuildServiceProvider();
        var httpClientFactory = new FakeHttpClientFactory(new Dictionary<string, HttpMessageHandler>(StringComparer.Ordinal)
        {
            ["cart"] = cartHandler,
            ["orders"] = ordersHandler
        });
        var tokenProvider = BuildTokenProvider();
        var request = new StorefrontEndpoints.CheckoutRequest(cartId, customerId, couponCode);

        await StorefrontEndpoints.CheckoutAsync(
            request,
            httpContext,
            httpClientFactory,
            tokenProvider,
            NullLoggerFactory.Instance,
            CancellationToken.None);

        httpContext.Response.Body.Position = 0;
        return httpContext;
    }

    private static string ReadBody(HttpContext httpContext)
    {
        httpContext.Response.Body.Position = 0;
        using var reader = new StreamReader(httpContext.Response.Body);
        return reader.ReadToEnd();
    }

    private static KeycloakTokenProvider BuildTokenProvider()
    {
        var handler = new RecordingHandler(_ => JsonResponse(HttpStatusCode.OK, new { access_token = "test-token", expires_in = 300 }));
        var httpClient = new HttpClient(handler) { BaseAddress = new Uri("http://keycloak.invalid") };
        var options = Options.Create(new KeycloakOptions
        {
            TokenUrl = "http://keycloak.invalid/realms/orders-lab/protocol/openid-connect/token",
            ClientId = "orders-api-clients",
            ClientSecret = "test-secret-not-a-real-credential"
        });
        return new KeycloakTokenProvider(httpClient, options);
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

    private sealed record RecordedRequest(HttpMethod Method, string? Body, string? AuthorizationHeader);

    /// <summary>
    /// Snapshots method/body/auth-header at Send time rather than storing
    /// the HttpRequestMessage itself - CheckoutAsync disposes it (a `using`
    /// around the request it builds, same as every other route in this
    /// service), so reading its Content afterwards would throw
    /// ObjectDisposedException.
    /// </summary>
    private sealed class RecordingHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public List<RecordedRequest> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
            Requests.Add(new RecordedRequest(request.Method, body, request.Headers.Authorization?.ToString()));
            return respond(request);
        }
    }
}
