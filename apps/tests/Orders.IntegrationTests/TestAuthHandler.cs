using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Orders.IntegrationTests;

/// <summary>
/// Stands in for the real Keycloak-backed JWT bearer scheme (see
/// OrdersApiFactory) so HTTP-level tests can authenticate as any caller
/// without a real Keycloak - a real token's actual validation
/// (signature, issuer, expiry) is BuildingBlocks.WebAuthentication's
/// concern, already outside what these tests exist to exercise. Reads two
/// request headers a test sets explicitly: X-Test-Roles (comma-separated,
/// mapped the same way OnTokenValidated maps realm_access.roles) and
/// X-Test-Subject (preferred_username, what CallerIdentityExtensions.
/// GetCustomerId reads first). Neither present means unauthenticated -
/// the same "no bearer token" case a real missing/absent header produces -
/// so a single scheme covers both the authenticated and anonymous paths a
/// test needs.
/// </summary>
public sealed class TestAuthHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    public const string SchemeName = "Test";
    public const string RolesHeader = "X-Test-Roles";
    public const string SubjectHeader = "X-Test-Subject";

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (!Request.Headers.TryGetValue(SubjectHeader, out var subject))
        {
            return Task.FromResult(AuthenticateResult.NoResult());
        }

        var claims = new List<Claim> { new("preferred_username", subject!) };
        if (Request.Headers.TryGetValue(RolesHeader, out var roles))
        {
            claims.AddRange(roles.ToString()
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(role => new Claim(ClaimTypes.Role, role)));
        }

        var identity = new ClaimsIdentity(claims, SchemeName);
        var ticket = new AuthenticationTicket(new ClaimsPrincipal(identity), SchemeName);
        return Task.FromResult(AuthenticateResult.Success(ticket));
    }
}
