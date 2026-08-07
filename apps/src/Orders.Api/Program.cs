using System.Security.Claims;
using System.Text.Json;
using BuildingBlocks;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Orders.Api.Authorization;
using Orders.Api.Endpoints;
using Orders.Api.Grpc;
using Orders.Api.Middleware;
using Orders.Api.RateLimiting;
using Orders.Application;
using Orders.Application.Pricing;
using Orders.Infrastructure;
using Orders.Infrastructure.Data;

var builder = WebApplication.CreateBuilder(args);
var instanceId = builder.Configuration["InstanceId"] ?? Environment.MachineName;

// Milestone 30: gRPC gets its own port (8081) rather than sharing 8080 with
// REST. Tried sharing first - it doesn't work here: the Milestone 26
// AuthorizationPolicy's Server resource for port 8080 hardcodes
// proxyProtocol: HTTP/1, so Linkerd's proxy rejects genuine HTTP/2 (gRPC)
// traffic on that port with "HTTP_1_1_REQUIRED" before it ever reaches
// Kestrel. A dedicated HTTP/2-only port (with its own Server resource
// declaring proxyProtocol: HTTP/2 or gRPC) is also just the standard
// real-world pattern - most gRPC services aren't multiplexed onto the same
// port as a REST API anyway. This service has no TLS in front of it either
// way (Linkerd terminates/originates mTLS transparently at the proxy - the
// app itself always speaks plain HTTP, matching every other service in
// this lab), so both ports are cleartext: HTTP/1.1 on 8080, h2c on 8081.
// Explicit ListenAnyIP calls for both ports, not ConfigureEndpointDefaults
// relying on ASPNETCORE_URLS for the REST port - the moment Kestrel is
// configured with any explicit Listen/ListenAnyIP call, it stops honoring
// the ASPNETCORE_URLS-derived endpoint entirely (logged as "Overriding
// address(es) ... Binding to endpoints defined via IConfiguration and/or
// UseKestrel() instead"), silently leaving 8080 unbound - found because
// the pod's readiness probe (which targets 8080) started failing and
// crash-looping it, and the boot log showed only "Now listening on:
// http://[::]:8081".
const int RestPort = 8080;
const int GrpcPort = 8081;
builder.WebHost.ConfigureKestrel(options =>
{
    options.ListenAnyIP(RestPort, listenOptions =>
    {
        listenOptions.Protocols = HttpProtocols.Http1;
    });
    options.ListenAnyIP(GrpcPort, listenOptions =>
    {
        listenOptions.Protocols = HttpProtocols.Http2;
    });
});

// Milestone 69: fail loudly at startup instead of silently at runtime.
//
// A missing DI registration used to surface only when something first
// tried to resolve it - and when that something is the outbox dispatcher,
// the failure is a background loop logging an exception every poll while
// the service reports healthy and quietly stops publishing every event.
// ValidateOnBuild turns that into a refusal to start. It is off by default
// outside Development; the cost is a slower boot, which is the right trade
// against an outbox that looks fine and delivers nothing.
builder.Host.UseDefaultServiceProvider(options =>
{
    options.ValidateOnBuild = true;
    options.ValidateScopes = true;
});

builder.Logging.ClearProviders();
builder.Logging.AddJsonConsole(options =>
{
    options.IncludeScopes = true;
    options.UseUtcTimestamp = true;
});
builder.Logging.AddOrdersOpenTelemetryLogging("orders-api", instanceId, builder.Environment.EnvironmentName);

builder.Services.AddOrdersObservability("orders-api", instanceId, builder.Environment.EnvironmentName);

builder.Services.AddProblemDetails();
builder.Services.AddOptions<KafkaOptions>()
    .Bind(builder.Configuration.GetSection(KafkaOptions.SectionName))
    .Validate(options => !string.IsNullOrWhiteSpace(options.BootstrapServers), "Kafka bootstrap servers are required.")
    .Validate(options => !string.IsNullOrWhiteSpace(options.OrderCreatedTopic), "Kafka topic is required.")
    .ValidateOnStart();

var connectionString = builder.Configuration.GetConnectionString("Orders")
    ?? throw new InvalidOperationException("Connection string 'Orders' is required.");

// Milestone 66: the promotion policy (coupon codes, category promotions,
// free-shipping threshold) is configuration, so a campaign changes without
// a redeploy. Absent config, PricingOptions' own defaults apply.
builder.Services.Configure<PricingOptions>(builder.Configuration.GetSection(PricingOptions.SectionName));
builder.Services.AddOptions<CatalogClientOptions>()
    .Bind(builder.Configuration.GetSection(CatalogClientOptions.SectionName))
    .Validate(options => !string.IsNullOrWhiteSpace(options.BaseUrl), "Catalog base URL is required.")
    .ValidateOnStart();

// Checkout cannot price an order without the catalog, so this call is on
// the critical path - hence the short timeout and the standard resilience
// handler rather than the best-effort treatment Orders.Worker gives the
// same client for bestseller tracking.
builder.Services.AddHttpClient<ICatalogClient, CatalogClient>((serviceProvider, client) =>
{
    var options = serviceProvider.GetRequiredService<IOptions<CatalogClientOptions>>().Value;
    client.BaseAddress = new Uri(options.BaseUrl);
    client.Timeout = TimeSpan.FromSeconds(3);
}).AddStandardResilienceHandler();

builder.Services.AddOrdersApplication();
builder.Services.AddOrdersInfrastructure(builder.Configuration, connectionString, instanceId);
builder.Services.AddOrdersRateLimiting(builder.Configuration);
builder.Services.AddGrpc();

// Milestone 26: bearer tokens are validated against Keycloak's own JWKS,
// fetched from its OIDC discovery document at startup and refreshed
// automatically - no shared secret or key material lives in this service's
// own configuration. "orders-api" is a hardcoded-audience protocol mapper
// on the Keycloak client (see scripts/keycloak-configure-realm.sh), not the
// client_credentials grant's default "account" audience, so a token minted
// for some other Keycloak client in this realm is rejected on audience
// alone, not just on missing roles.
var authority = builder.Configuration["Authentication:Authority"]
    ?? throw new InvalidOperationException("Authentication:Authority is required.");
builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(options =>
    {
        options.Authority = authority;
        options.Audience = "orders-api";
        options.RequireHttpsMetadata = false;
        // Keycloak puts realm roles in a nested "realm_access": { "roles": [...] }
        // claim, not as flat role claims - RequireRole() below checks
        // ClaimTypes.Role, which nothing populates by default. Without this,
        // every request authenticates fine but every role-based policy fails
        // (403, not 401) regardless of the token's actual roles.
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
builder.Services.AddAuthorizationBuilder()
    .AddPolicy(OrdersAuthorizationPolicies.Read, policy => policy.RequireRole("orders:read", "orders:write"))
    .AddPolicy(OrdersAuthorizationPolicies.Write, policy => policy.RequireRole("orders:write"));
builder.Services.AddHealthChecks()
    .AddTypeActivatedCheck<PostgresHealthCheck>("postgres", failureStatus: null, tags: ["ready"], args: ["Orders"])
    .AddCheck<KafkaHealthCheck>("kafka", tags: ["ready"])
    .AddCheck<RedisHealthCheck>("redis", tags: ["ready"]);

var app = builder.Build();

if (args.Contains("--migrate", StringComparer.Ordinal))
{
    await using var scope = app.Services.CreateAsyncScope();
    var dbContext = scope.ServiceProvider.GetRequiredService<OrdersDbContext>();
    await dbContext.Database.MigrateAsync();
    return;
}

app.UseExceptionHandler();
app.UseRateLimiter();
app.UseMiddleware<DistributedRateLimitingMiddleware>();
app.UseAuthentication();
app.UseAuthorization();
app.UseMiddleware<CorrelationIdMiddleware>();
app.Use(async (context, next) =>
{
    context.Response.Headers["X-Instance-ID"] = instanceId;
    await next(context);
});

app.MapHealthChecks("/health/live", new HealthCheckOptions
{
    Predicate = _ => false
});
app.MapHealthChecks("/health/ready", new HealthCheckOptions
{
    Predicate = registration => registration.Tags.Contains("ready")
});

app.MapGet("/", () => Results.Ok(new { service = "Orders.Api", instanceId }));
app.MapOrderEndpoints();
app.MapFulfillmentEndpoints();
app.MapReturnEndpoints();
app.MapOrderSummaryEndpoints();
app.MapOrderHistoryEndpoints();
app.MapGrpcService<OrderQueryGrpcService>();

await app.RunAsync();

public partial class Program;
