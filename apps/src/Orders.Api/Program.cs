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
// REST - the Milestone 26 AuthorizationPolicy hardcodes proxyProtocol:
// HTTP/1 on 8080, so Linkerd rejects real HTTP/2 (gRPC) traffic there.
// Both ports are cleartext (Linkerd terminates mTLS at the proxy). Both
// need an explicit ListenAnyIP call: the moment Kestrel gets one explicit
// Listen call it stops honoring ASPNETCORE_URLS entirely, which silently
// left 8080 unbound and crash-looped the pod's readiness probe before this
// was explicit.
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

// Milestone 69: fail loudly at startup instead of silently at runtime. A
// missing DI registration used to surface only when first resolved - if
// that's the outbox dispatcher, it's a background loop logging an
// exception every poll while the service reports healthy and delivers
// nothing. ValidateOnBuild trades a slower boot for refusing to start instead.
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

// On checkout's critical path (no catalog, no price), unlike the
// best-effort treatment Orders.Worker gives this same client for bestsellers.
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
// fetched from its OIDC discovery document and refreshed automatically - no
// key material lives in this service's config. "orders-api" is a
// hardcoded-audience protocol mapper (scripts/keycloak-configure-realm.sh),
// not the client_credentials grant's default "account" audience, so a
// token minted for another client is rejected on audience alone.
var authority = builder.Configuration["Authentication:Authority"]
    ?? throw new InvalidOperationException("Authentication:Authority is required.");
builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(options =>
    {
        options.Authority = authority;
        options.Audience = "orders-api";
        options.RequireHttpsMetadata = false;
        // Keycloak nests realm roles under "realm_access": { "roles": [...] },
        // not as flat claims; without this, RequireRole() below always 403s.
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
