using System.Net;
using System.Text;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Orders.Worker;

namespace Orders.UnitTests;

/// <summary>Orders.Worker's own service identity for the anti-entropy sweep's cross-service reads; a trusted-backend-caller use case, not a shopper stand-in.</summary>
public sealed class KeycloakTokenProviderTests
{
    [Fact]
    public async Task GetTokenAsyncFetchesOnceAndServesSubsequentCallsFromCache()
    {
        var handler = new CountingTokenHandler(expiresIn: 300);
        var provider = CreateProvider(handler);

        var first = await provider.GetTokenAsync(CancellationToken.None);
        var second = await provider.GetTokenAsync(CancellationToken.None);
        var third = await provider.GetTokenAsync(CancellationToken.None);

        Assert.Equal("token-1", first);
        Assert.Equal(first, second);
        Assert.Equal(first, third);
        Assert.Equal(1, handler.CallCount);
    }

    [Fact]
    public async Task GetTokenAsyncRefetchesAfterTheCachedTokenExpires()
    {
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 8, 17, 12, 0, 0, TimeSpan.Zero));
        var handler = new CountingTokenHandler(expiresIn: 60);
        var provider = CreateProvider(handler, clock);

        var first = await provider.GetTokenAsync(CancellationToken.None);
        clock.Advance(TimeSpan.FromSeconds(31));
        var second = await provider.GetTokenAsync(CancellationToken.None);

        Assert.Equal("token-1", first);
        Assert.Equal("token-2", second);
        Assert.Equal(2, handler.CallCount);
    }

    private static KeycloakTokenProvider CreateProvider(HttpMessageHandler handler, TimeProvider? timeProvider = null)
    {
        var httpClient = new HttpClient(handler) { BaseAddress = new Uri("http://keycloak.invalid") };
        var options = Options.Create(new KeycloakOptions
        {
            TokenUrl = "http://keycloak.invalid/realms/orders-lab/protocol/openid-connect/token",
            ClientId = "orders-api-clients",
            ClientSecret = "test-secret-not-a-real-credential"
        });
        return new KeycloakTokenProvider(httpClient, options, timeProvider);
    }

    private sealed class CountingTokenHandler(int expiresIn) : HttpMessageHandler
    {
        public int CallCount { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            CallCount++;
            var payload = $$"""{"access_token":"token-{{CallCount}}","expires_in":{{expiresIn}}}""";
            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(payload, Encoding.UTF8, "application/json")
            };
            return Task.FromResult(response);
        }
    }
}
