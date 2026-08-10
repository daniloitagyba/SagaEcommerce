using PactNet.Verifier;
using Xunit;

namespace Orders.ContractTests;

/// <summary>
/// Verifies the pact OrdersApiConsumerTests generated against a real,
/// running Orders.Api - deliberately not hermetic, since the whole point
/// of provider verification is checking the *real* deployed service.
/// GitHub Actions runners have no route to this lab's server, so it's run
/// explicitly against a live deployment - see
/// docs/cicd/milestone-29-contract-testing.md. Requires ORDERS_API_URL and
/// ACCESS_TOKEN; reports as skipped (not passed, not failed) when unset, so
/// "not configured" and "verification failed" stay visibly different outcomes.
/// xunit v2 has no built-in dynamic skip (Assert.Skip/SkipException.ForSkip
/// throw the right "$XunitDynamicSkip$" marker, but the v2 execution engine
/// doesn't act on it and reports Failed) - Xunit.SkippableFact supplies a
/// [SkippableFact] + Skip.If that xunit v2 actually honors as Skipped.
/// </summary>
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

        // Recorded interactions carry a fake bearer token; the header override makes replaying them against the real, auth-enforcing Orders.Api work.
        new PactVerifier("OrdersApi")
            .WithHttpEndpoint(new Uri(providerUrl!))
            .WithFileSource(new FileInfo(pactPath))
            .WithRequestTimeout(TimeSpan.FromSeconds(10))
            .WithCustomHeader("Authorization", $"Bearer {accessToken}")
            .Verify();
    }
}
