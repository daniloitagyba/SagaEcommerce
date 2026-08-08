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
    /// </summary>
    public static AuthenticationBuilder AddKeycloakJwtBearer(
        this IServiceCollection services, IConfiguration configuration, string audience)
    {
        var authority = configuration["Authentication:Authority"]
            ?? throw new InvalidOperationException("Authentication:Authority is required.");

        return services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
            .AddJwtBearer(options =>
            {
                options.Authority = authority;
                options.Audience = audience;
                options.RequireHttpsMetadata = false;
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
