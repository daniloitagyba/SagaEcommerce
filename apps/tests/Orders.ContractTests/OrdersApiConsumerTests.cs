using System.Net;
using System.Net.Http.Json;
using PactNet;
using PactNet.Matchers;

namespace Orders.ContractTests;

/// <summary>Consumer-driven contract testing for Orders.Api's REST surface; generates the pact against a mock server (see OrdersApiProviderTests for the provider-side verification).</summary>
public sealed class OrdersApiConsumerTests
{
    private readonly IPactBuilderV3 _pact;

    public OrdersApiConsumerTests()
    {
        var config = new PactConfig
        {
            PactDir = Path.Combine("..", "..", "..", "..", "..", "pacts"),
            LogLevel = PactLogLevel.Warn
        };

        _pact = Pact.V3("OrdersClient", "OrdersApi", config).WithHttpInteractions();
    }

    [Fact]
    public async Task CreateOrderReturnsTheCreatedOrder()
    {
        _pact
            .UponReceiving("a request to create an order")
            .Given("the orders API is available and the caller has the orders:write role")
            .WithRequest(HttpMethod.Post, "/orders")
            .WithHeader("Content-Type", "application/json")
            .WithHeader("Authorization", Match.Regex("Bearer contract-test-token", "Bearer .+"))
            .WithJsonBody(new
            {
                customerId = "contract-test-customer",
                items = new[]
                {
                    new { sku = "SKU-BOOK-002", quantity = 1 }
                }
            })
            .WillRespond()
            .WithStatus(HttpStatusCode.Created)
            .WithHeader("Content-Type", "application/json; charset=utf-8")
            .WithJsonBody(new
            {
                id = Match.Type("11111111-1111-1111-1111-111111111111"),
                customerId = Match.Type("contract-test-customer"),
                amount = Match.Decimal(49.90m),
                currency = Match.Type("BRL"),
                status = Match.Type("Created"),
                createdAt = Match.Type("2026-01-01T00:00:00+00:00"),
                correlationId = Match.Type("11111111111111111111111111111111"),
                instanceId = Match.Type("orders-api-000000000-00000")
            });

        await _pact.VerifyAsync(async ctx =>
        {
            using var client = new HttpClient { BaseAddress = ctx.MockServerUri };
            client.DefaultRequestHeaders.Add("Authorization", "Bearer contract-test-token");

            var response = await client.PostAsJsonAsync(
                "/orders",
                new
                {
                    customerId = "contract-test-customer",
                    items = new[]
                    {
                        new { sku = "SKU-BOOK-002", quantity = 1 }
                    }
                });

            Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        });
    }

    [Fact]
    public async Task GetOrderWhenTheOrderDoesNotExistReturnsNotFound()
    {
        var orderId = "22222222-2222-2222-2222-222222222222";

        _pact
            .UponReceiving("a request for an order that does not exist")
            .Given("no order with this id exists, and the caller has the orders:read role")
            .WithRequest(HttpMethod.Get, $"/orders/{orderId}")
            .WithHeader("Authorization", Match.Regex("Bearer contract-test-token", "Bearer .+"))
            .WillRespond()
            .WithStatus(HttpStatusCode.NotFound);

        await _pact.VerifyAsync(async ctx =>
        {
            using var client = new HttpClient { BaseAddress = ctx.MockServerUri };
            client.DefaultRequestHeaders.Add("Authorization", "Bearer contract-test-token");

            var response = await client.GetAsync($"/orders/{orderId}");

            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        });
    }
}
