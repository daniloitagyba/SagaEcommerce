using System.Net;
using Microsoft.AspNetCore.Http;
using Storefront.Service;

namespace Storefront.UnitTests;

/// <summary>
/// Regression coverage for the bug behind "place order didn't work": the
/// storefront created the order fine (POST /api/orders -> 201) but every
/// follow-up read - GET /api/orders/{id}, GET /api/orders/{id}/history,
/// GET /api/orders/summary - 404d, because ProxyEndpoints.ForwardAsync
/// forwarded the wildcard-captured {**path} bare, silently dropping the
/// "orders/" segment Orders.Api's own routes all live under. A shopper
/// saw "This order could not be found" immediately after placing it.
///
/// Exercises ForwardOrdersSubPathAsync directly - the same method the
/// MapGet/MapPost("/api/orders/{**path}") lambdas call - passing exactly
/// the {**path} value ASP.NET's router would have captured (no leading
/// "orders", the route template already consumed that literal segment),
/// so this fails the same way production did if the "orders/" prefix is
/// ever removed.
/// </summary>
public sealed class OrdersProxyEndpointTests
{
    [Theory]
    [InlineData("11111111-1111-1111-1111-111111111111", "/orders/11111111-1111-1111-1111-111111111111")]
    [InlineData("11111111-1111-1111-1111-111111111111/history", "/orders/11111111-1111-1111-1111-111111111111/history")]
    [InlineData("summary", "/orders/summary")]
    public async Task GetForwardsToTheOrdersPrefixedUpstreamPath(string capturedPath, string expectedUpstreamPath)
    {
        var handler = new RecordingHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("{}")
        });

        var httpContext = await InvokeGetAsync(capturedPath, handler);

        Assert.Equal(StatusCodes.Status200OK, httpContext.Response.StatusCode);
        var request = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Get, request.Method);
        Assert.Equal(expectedUpstreamPath, request.RequestUri!.AbsolutePath);
    }

    [Fact]
    public async Task PostForwardsSubPathsLikeCancellationToTheOrdersPrefixedUpstreamPath()
    {
        var handler = new RecordingHandler(_ => new HttpResponseMessage(HttpStatusCode.NoContent));

        var httpContext = await InvokePostAsync("11111111-1111-1111-1111-111111111111/cancellation", handler);

        Assert.Equal(StatusCodes.Status204NoContent, httpContext.Response.StatusCode);
        var request = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.Equal("/orders/11111111-1111-1111-1111-111111111111/cancellation", request.RequestUri!.AbsolutePath);
    }

    [Fact]
    public async Task AnUpstream404IsRelayedAsIsNotMaskedAsSomethingElse()
    {
        var handler = new RecordingHandler(_ => new HttpResponseMessage(HttpStatusCode.NotFound));

        var httpContext = await InvokeGetAsync("nonexistent-order-id", handler);

        Assert.Equal(StatusCodes.Status404NotFound, httpContext.Response.StatusCode);
    }

    [Fact]
    public async Task AnOversizedWriteIsRejectedBeforeCallingOrders()
    {
        var handler = new RecordingHandler(_ => throw new InvalidOperationException("must not be called"));
        var httpContext = new DefaultHttpContext { Response = { Body = new MemoryStream() } };
        httpContext.Request.Method = HttpMethods.Post;
        httpContext.Request.ContentLength = (5 * 1024 * 1024) + 1;

        await ProxyEndpoints.ForwardOrdersSubPathAsync(
            "11111111-1111-1111-1111-111111111111/cancellation",
            httpContext.Request,
            new FakeHttpClientFactory(handler),
            CancellationToken.None);

        Assert.Equal(StatusCodes.Status413PayloadTooLarge, httpContext.Response.StatusCode);
        Assert.Empty(handler.Requests);
    }

    private static Task<DefaultHttpContext> InvokeGetAsync(string capturedPath, RecordingHandler handler) =>
        InvokeAsync(HttpMethods.Get, capturedPath, handler);

    private static Task<DefaultHttpContext> InvokePostAsync(string capturedPath, RecordingHandler handler) =>
        InvokeAsync(HttpMethods.Post, capturedPath, handler);

    private static async Task<DefaultHttpContext> InvokeAsync(string method, string capturedPath, RecordingHandler handler)
    {
        var httpContext = new DefaultHttpContext { Response = { Body = new MemoryStream() } };
        httpContext.Request.Method = method;

        var httpClientFactory = new FakeHttpClientFactory(handler);

        await ProxyEndpoints.ForwardOrdersSubPathAsync(capturedPath, httpContext.Request, httpClientFactory, CancellationToken.None);

        httpContext.Response.Body.Position = 0;
        return httpContext;
    }

    private sealed class FakeHttpClientFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) =>
            new(handler) { BaseAddress = new Uri("http://orders-api-1:8080") };
    }

    private sealed record RecordedRequest(HttpMethod Method, Uri? RequestUri);

    /// <summary>Snapshots method/URI at Send time - the real code disposes the HttpRequestMessage afterward.</summary>
    private sealed class RecordingHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public List<RecordedRequest> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(new RecordedRequest(request.Method, request.RequestUri));
            return Task.FromResult(respond(request));
        }
    }
}
