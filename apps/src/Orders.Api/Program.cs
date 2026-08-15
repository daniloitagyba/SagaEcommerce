using BuildingBlocks;
using BuildingBlocks.WebAuthentication;
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
using Orders.Application.UseCases.ReturnOrder;
using Orders.Infrastructure;
using Orders.Infrastructure.Data;

var builder = WebApplication.CreateBuilder(args);
var instanceId = builder.Configuration["InstanceId"] ?? Environment.MachineName;

// gRPC gets its own port (8081) rather than sharing 8080 with
// REST - the AuthorizationPolicy hardcodes proxyProtocol:
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

// Fail loudly at startup instead of silently at runtime. A
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

builder.Services.AddExceptionHandler<BadHttpRequestExceptionHandler>();
builder.Services.AddProblemDetails();
builder.Services.AddOptions<KafkaOptions>()
    .Bind(builder.Configuration.GetSection(KafkaOptions.SectionName))
    .Validate(options => !string.IsNullOrWhiteSpace(options.BootstrapServers), "Kafka bootstrap servers are required.")
    .Validate(options => !string.IsNullOrWhiteSpace(options.OrderCreatedTopic), "Kafka topic is required.")
    .ValidateOnStart();

var connectionString = builder.Configuration.GetConnectionString("Orders")
    ?? throw new InvalidOperationException("Connection string 'Orders' is required.");

// The promotion policy (coupon codes, category promotions,
// free-shipping threshold) is configuration, so a campaign changes without
// a redeploy. Absent config, PricingOptions' own defaults apply.
builder.Services.AddOptions<PricingOptions>()
    .Bind(builder.Configuration.GetSection(PricingOptions.SectionName))
    .Validate(options => options.BulkQuantityThreshold > 0, "Bulk quantity threshold must be positive.")
    .Validate(options => options.BulkDiscountPercentage is >= 0m and <= 100m, "Bulk discount percentage must be between 0 and 100.")
    .Validate(options => options.CategoryDiscounts.All(entry => !string.IsNullOrWhiteSpace(entry.Key) && entry.Value is >= 0m and <= 100m), "Category discounts require a category and a percentage between 0 and 100.")
    .Validate(options => options.ShippingByPostalPrefix.All(entry => entry.Key.Length == 2 && entry.Key.All(char.IsDigit) && entry.Value >= 0m), "Shipping prefixes must contain two digits and a non-negative amount.")
    .Validate(options => options.DefaultShippingAmount >= 0m && options.FlatShippingAmount >= 0m, "Shipping amounts must not be negative.")
    .Validate(options => options.TaxRateByRegion.All(entry => entry.Key.Length == 2 && entry.Key.All(char.IsLetter) && entry.Value is >= 0m and <= 100m), "Regional tax rates require a two-letter region and a percentage between 0 and 100.")
    .Validate(options => options.TaxRatePercentage is >= 0m and <= 100m, "Tax rate must be between 0 and 100.")
    .Validate(options => options.FreeShippingThreshold >= 0m, "Free-shipping threshold must not be negative.")
    .ValidateOnStart();
// How long a Regret return still owes shipping. Absent config, ReturnOptions' own 7-day default applies.
builder.Services.AddOptions<ReturnOptions>()
    .Bind(builder.Configuration.GetSection(ReturnOptions.SectionName))
    .Validate(options => options.RegretWindowDays > 0, "Regret return window must be positive.")
    .ValidateOnStart();
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
}).AddCriticalHttpResilience();

builder.Services.AddOrdersApplication();
builder.Services.AddOrdersInfrastructure(builder.Configuration, connectionString, instanceId);
builder.Services.AddOrdersRateLimiting(builder.Configuration);
builder.Services.AddGrpc();

// The JWT bearer wiring itself now lives in
// BuildingBlocks.WebAuthentication, shared with Catalog/Inventory/Cart -
// only the audience ever differed between them. "orders-api" is a
// hardcoded-audience protocol mapper (scripts/keycloak-configure-realm.sh),
// not the client_credentials grant's default "account" audience, so a
// token minted for another client is rejected on audience alone.
builder.Services.AddKeycloakJwtBearer(builder.Configuration, audience: "orders-api");
builder.Services.AddAuthorizationBuilder()
    // Orders:admin satisfies both - an admin token needs one role, not three.
    .AddPolicy(OrdersAuthorizationPolicies.Read, policy => policy.RequireRole("orders:read", "orders:write", OrdersAuthorizationPolicies.Admin))
    .AddPolicy(OrdersAuthorizationPolicies.Write, policy => policy.RequireRole("orders:write", OrdersAuthorizationPolicies.Admin))
    .AddPolicy(OrdersAuthorizationPolicies.Admin, policy => policy.RequireRole(OrdersAuthorizationPolicies.Admin));
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
// First, not last: this used to run after rate limiting and authentication,
// so the responses most worth tracing during an incident - 429 (shed load),
// 401/403 (rejected auth) - went out with no correlation id at all.
app.UseMiddleware<CorrelationIdMiddleware>();
app.UseRateLimiter();
app.UseAuthentication();
// After authentication, not before: DistributedRateLimitingMiddleware keys
// its bucket by the caller's own identity (customer id, or client_id for a
// service account), which only exists once auth has populated context.User.
app.UseMiddleware<DistributedRateLimitingMiddleware>();
app.UseAuthorization();
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
app.MapCancellationEndpoints();
app.MapReturnEndpoints();
app.MapOrderSummaryEndpoints();
app.MapOrderHistoryEndpoints();
app.MapGrpcService<OrderQueryGrpcService>();

await app.RunAsync();

public partial class Program;
