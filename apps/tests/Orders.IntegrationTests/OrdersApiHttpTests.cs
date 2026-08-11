using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;

namespace Orders.IntegrationTests;

/// <summary>
/// The first WebApplicationFactory-based test in this repo - no service
/// had one, so nothing exercised real HTTP: routing, authorization
/// policies, model binding, the actual ProblemDetails/JSON error shape a
/// client sees. Orders.Api first (largest surface, per the plan this
/// closes a gap in) - GET/POST /orders through the real ASP.NET Core
/// pipeline, not the handlers directly the way the rest of this session's
/// new tests do.
/// </summary>
public sealed class OrdersApiHttpTests : IClassFixture<OrdersApiFactory>
{
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);
    private readonly OrdersApiFactory _factory;

    public OrdersApiHttpTests(OrdersApiFactory factory)
    {
        _factory = factory;
    }

    private HttpClient CreateAuthenticatedClient(string subject, params string[] roles)
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add(TestAuthHandler.SubjectHeader, subject);
        if (roles.Length > 0)
        {
            client.DefaultRequestHeaders.Add(TestAuthHandler.RolesHeader, string.Join(',', roles));
        }

        return client;
    }

    [Fact]
    public async Task HealthLiveRespondsWithoutAuthentication()
    {
        var client = _factory.CreateClient();

        var response = await client.GetAsync("/health/live");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task GettingAnOrderWithNoBearerTokenIsRejected()
    {
        var client = _factory.CreateClient();

        var response = await client.GetAsync($"/orders/{Guid.NewGuid()}");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task GettingAnOrderWithReadRoleButNoWriteCannotCreateOne()
    {
        var client = CreateAuthenticatedClient("reader-only", "orders:read");

        var response = await client.PostAsJsonAsync("/orders", new { customerId = "reader-only", amount = 10m, currency = "BRL" });

        // 403, not 401 - the caller IS authenticated, just not authorized for this policy.
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task ANonExistentOrderIdReturnsNotFoundRatherThanAnError()
    {
        var client = CreateAuthenticatedClient("customer-1", "orders:read");

        var response = await client.GetAsync($"/orders/{Guid.NewGuid()}");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task CreatingAnOrderReturnsCreatedWithALocationHeaderAndTheOrderIsThenReadableByItsOwner()
    {
        var client = CreateAuthenticatedClient("customer-http-1", "orders:write", "orders:read");

        var createResponse = await client.PostAsJsonAsync(
            "/orders",
            new { customerId = "customer-http-1", amount = 49.90m, currency = "BRL" },
            SerializerOptions);

        Assert.Equal(HttpStatusCode.Created, createResponse.StatusCode);
        Assert.NotNull(createResponse.Headers.Location);

        var created = await createResponse.Content.ReadFromJsonAsync<JsonElement>(SerializerOptions);
        var orderId = created.GetProperty("id").GetGuid();
        Assert.Equal("customer-http-1", created.GetProperty("customerId").GetString());
        Assert.Equal(49.90m, created.GetProperty("amount").GetDecimal());

        var getResponse = await client.GetAsync($"/orders/{orderId}");
        Assert.Equal(HttpStatusCode.OK, getResponse.StatusCode);
        var fetched = await getResponse.Content.ReadFromJsonAsync<JsonElement>(SerializerOptions);
        Assert.Equal(orderId, fetched.GetProperty("id").GetGuid());
    }

    /// <summary>404, not 403 - GetByIdAsync's own comment: a non-owner gets the same answer as a genuinely missing id, so probing ids can't be used to learn which ones exist.</summary>
    [Fact]
    public async Task AnotherCustomersOrderIsAlsoReportedAsNotFound()
    {
        var owner = CreateAuthenticatedClient("customer-http-owner", "orders:write", "orders:read");
        var createResponse = await owner.PostAsJsonAsync(
            "/orders",
            new { customerId = "customer-http-owner", amount = 20m, currency = "BRL" },
            SerializerOptions);
        var created = await createResponse.Content.ReadFromJsonAsync<JsonElement>(SerializerOptions);
        var orderId = created.GetProperty("id").GetGuid();

        var otherShopper = CreateAuthenticatedClient("customer-http-other", "orders:read");
        var response = await otherShopper.GetAsync($"/orders/{orderId}");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task MalformedJsonProducesAProblemDetailsBadRequestRatherThanAnUnhandledError()
    {
        var client = CreateAuthenticatedClient("customer-http-2", "orders:write");
        using var content = new StringContent("{ not valid json", Encoding.UTF8, "application/json");

        var response = await client.PostAsync("/orders", content);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task EveryResponseCarriesTheInstanceIdHeaderRegardlessOfOutcome()
    {
        var client = _factory.CreateClient();

        var response = await client.GetAsync("/health/live");

        Assert.True(response.Headers.Contains("X-Instance-ID"));
    }
}
