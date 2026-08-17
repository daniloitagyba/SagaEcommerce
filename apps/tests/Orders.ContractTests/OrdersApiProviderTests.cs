using PactNet.Verifier;
using Xunit;

namespace Orders.ContractTests;

/// <summary>Verifies the pact OrdersApiConsumerTests generated against a real, running Orders.Api; requires ORDERS_API_URL and ACCESS_TOKEN, reporting Skipped (via Xunit.SkippableFact) when unset.</summary>
public sealed class OrdersApiProviderTests
{
    [SkippableFact]
    public void VerifyOrdersApiAgainstTheGeneratedPact()
    {
        var providerUrl = Environment.GetEnvironmentVariable("ORDERS_API_URL");
        var accessToken = Environment.GetEnvironmentVariable("ACCESS_TOKEN");

        Skip.If(
            string.IsNullOrWhiteSpace(providerUrl) || string.IsNullOrWhiteSpace(accessToken),
            "ORDERS_API_URL and ACCESS_TOKEN must both be set to a live deployment and a valid " +
            "bearer token. See docs/cicd/milestone-29-contract-testing.md.");

        var pactPath = Path.Combine("..", "..", "..", "..", "..", "pacts", "OrdersClient-OrdersApi.json");

        new PactVerifier("OrdersApi")
            .WithHttpEndpoint(new Uri(providerUrl!))
            .WithFileSource(new FileInfo(pactPath))
            .WithRequestTimeout(TimeSpan.FromSeconds(10))
            .WithCustomHeader("Authorization", $"Bearer {accessToken}")
            .Verify();
    }
}
