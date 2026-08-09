using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace BuildingBlocks.WebAuthentication;

/// <summary>
/// The JWT bearer wiring Catalog.Service,
/// Inventory.Service, Cart.Service and Orders.Api each carried near-verbatim
/// in their own Program.cs - named at the time as
/// a real cost of that approach, not missed. One copy now, one
/// per service's own Audience being the only thing that ever actually
/// differed between them.
/// </summary>
public static class KeycloakJwtBearerExtensions
{
    /// <summary>
    /// Bearer tokens are validated against Keycloak's own
    /// JWKS, fetched from its OIDC discovery document and refreshed
    /// automatically - no key material lives in any service's config.
    /// <paramref name="audience"/> is a hardcoded-audience protocol mapper
    /// (scripts/keycloak-configure-realm.sh) per client, not the
    /// client_credentials grant's default "account" audience, so a token
    /// minted for another client is rejected on audience alone. Keycloak
    /// nests realm roles under "realm_access": { "roles": [...] }, not as
    /// flat claims; without the OnTokenValidated below, RequireRole() always 403s.
    ///
    /// Authentication:AlternateAuthority (optional) is a second issuer this
    /// service also trusts, without changing where it fetches JWKS from
    /// (still Authentication:Authority - that address has to be reachable
    /// from inside the service's own network either way). It exists for
    /// exactly one reason: a shopper's browser and this service reach
    /// Keycloak by different addresses (the service via the internal
    /// "keycloak" Docker DNS name, a browser via Keycloak's LAN-published
    /// port), and with KC_HOSTNAME_STRICT=false and no fixed KC_HOSTNAME,
    /// Keycloak mints a token whose "iss" claim reflects whichever address
    /// actually issued it - so a token minted through the browser's path
    /// carries an issuer this service's default single-Authority validation
    /// would otherwise reject outright.
    /// </summary>
    public static AuthenticationBuilder AddKeycloakJwtBearer(
        this IServiceCollection services, IConfiguration configuration, string audience)
    {
        var authority = configuration["Authentication:Authority"]
            ?? throw new InvalidOperationException("Authentication:Authority is required.");
        var alternateAuthority = configuration["Authentication:AlternateAuthority"];

        return services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
            .AddJwtBearer(options =>
            {
                options.Authority = authority;
                options.Audience = audience;
                options.RequireHttpsMetadata = false;
                if (!string.IsNullOrWhiteSpace(alternateAuthority))
                {
                    options.TokenValidationParameters.ValidIssuers = [authority, alternateAuthority];
                }
                options.Events = new JwtBearerEvents
                {
                    OnTokenValidated = context =>
                    {
                        var realmAccess = context.Principal?.FindFirst("realm_access")?.Value;
                        if (string.IsNullOrEmpty(realmAccess) || context.Principal?.Identity is not ClaimsIdentity identity)
                        {
                            return Task.CompletedTask;
                        }

                        using var document = JsonDocument.Parse(realmAccess);
                        if (document.RootElement.TryGetProperty("roles", out var roles))
                        {
                            foreach (var role in roles.EnumerateArray())
                            {
                                identity.AddClaim(new Claim(ClaimTypes.Role, role.GetString() ?? string.Empty));
                            }
                        }

                        return Task.CompletedTask;
                    }
                };
            });
    }
}
