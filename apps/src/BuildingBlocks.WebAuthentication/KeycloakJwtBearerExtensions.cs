using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace BuildingBlocks.WebAuthentication;

/// <summary>The JWT bearer wiring shared across services, with each service's own Audience the only thing that differs.</summary>
public static class KeycloakJwtBearerExtensions
{
    /// <summary>Validates bearer tokens against Keycloak's JWKS and promotes realm_access roles into role claims.</summary>
    /// <param name="audience">The audience this service's tokens must carry.</param>
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
