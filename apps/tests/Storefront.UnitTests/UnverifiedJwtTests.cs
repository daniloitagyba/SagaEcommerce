using System.Text;
using System.Text.Json;
using Storefront.Service;

namespace Storefront.UnitTests;

/// <summary>
/// Milestone 85: the one thing this reads a token for - a stable string to
/// build an Idempotency-Key from. Never used for authorization (Orders.Api
/// and Cart.Service verify the same forwarded token fully); malformed
/// input degrading to null rather than throwing is the load-bearing property.
/// </summary>
public class UnverifiedJwtTests
{
    private static string Token(object payload)
    {
        var json = JsonSerializer.Serialize(payload);
        var segment = Convert.ToBase64String(Encoding.UTF8.GetBytes(json)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        return $"header.{segment}.signature";
    }

    [Fact]
    public void ReadsAClaimFromABearerToken()
    {
        var token = $"Bearer {Token(new { preferred_username = "customer-1" })}";

        Assert.Equal("customer-1", UnverifiedJwt.TryGetClaim(token, "preferred_username"));
    }

    [Fact]
    public void ReadsAClaimFromATokenWithNoBearerPrefix()
    {
        var token = Token(new { sub = "customer-2" });

        Assert.Equal("customer-2", UnverifiedJwt.TryGetClaim(token, "sub"));
    }

    [Fact]
    public void MissingClaimReturnsNullNotAnException()
    {
        var token = $"Bearer {Token(new { sub = "customer-3" })}";

        Assert.Null(UnverifiedJwt.TryGetClaim(token, "preferred_username"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not-a-jwt")]
    [InlineData("Bearer not.valid-base64!!!.signature")]
    public void MalformedInputDegradesToNullRatherThanThrowing(string? token)
    {
        Assert.Null(UnverifiedJwt.TryGetClaim(token, "sub"));
    }
}
