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

const int RestPort = 8080;
const int GrpcPort = 8081;
builder.WebHost.ConfigureKestrel(options =>
{
    options.Limits.MaxRequestBodySize = 5 * 1024 * 1024;
    options.ListenAnyIP(RestPort, listenOptions =>
    {
        listenOptions.Protocols = HttpProtocols.Http1;
    });
    options.ListenAnyIP(GrpcPort, listenOptions =>
    {
        listenOptions.Protocols = HttpProtocols.Http2;
    });
});

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
builder.Services.AddExceptionHandler<InfrastructureUnavailableExceptionHandler>();
builder.Services.AddProblemDetails();
builder.Services.AddOptions<KafkaOptions>()
    .Bind(builder.Configuration.GetSection(KafkaOptions.SectionName))
    .Validate(options => !string.IsNullOrWhiteSpace(options.BootstrapServers), "Kafka bootstrap servers are required.")
    .Validate(options => !string.IsNullOrWhiteSpace(options.OrderCreatedTopic), "Kafka topic is required.")
    .ValidateOnStart();

var connectionString = builder.Configuration.GetConnectionString("Orders")
    ?? throw new InvalidOperationException("Connection string 'Orders' is required.");

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
builder.Services.AddOptions<ReturnOptions>()
    .Bind(builder.Configuration.GetSection(ReturnOptions.SectionName))
    .Validate(options => options.RegretWindowDays > 0, "Regret return window must be positive.")
    .ValidateOnStart();
builder.Services.AddOptions<CatalogClientOptions>()
    .Bind(builder.Configuration.GetSection(CatalogClientOptions.SectionName))
    .Validate(options => !string.IsNullOrWhiteSpace(options.BaseUrl), "Catalog base URL is required.")
    .ValidateOnStart();

builder.Services.AddHttpClient<ICatalogClient, CatalogClient>((serviceProvider, client) =>
{
    var options = serviceProvider.GetRequiredService<IOptions<CatalogClientOptions>>().Value;
    client.BaseAddress = new Uri(options.BaseUrl);
}).AddCriticalHttpResilience();

builder.Services.AddOrdersApplication();
builder.Services.AddOrdersInfrastructure(builder.Configuration, connectionString, instanceId);
builder.Services.AddOrdersRateLimiting(builder.Configuration);
builder.Services.AddGrpc();

builder.Services.AddKeycloakJwtBearer(builder.Configuration, audience: "orders-api");
builder.Services.AddAuthorizationBuilder()
    .AddPolicy(OrdersAuthorizationPolicies.Read, policy => policy.RequireRole("orders:read", "orders:write", OrdersAuthorizationPolicies.Admin))
    .AddPolicy(OrdersAuthorizationPolicies.Write, policy => policy.RequireRole("orders:write", OrdersAuthorizationPolicies.Admin))
    .AddPolicy(OrdersAuthorizationPolicies.Admin, policy => policy.RequireRole(OrdersAuthorizationPolicies.Admin));
builder.Services.AddSingleton<KafkaHealthCheck>();
builder.Services.AddHealthChecks()
    .AddTypeActivatedCheck<PostgresHealthCheck>("postgres", failureStatus: null, tags: ["ready"], args: ["Orders"])
    .AddCheck<KafkaHealthCheck>("kafka", tags: ["live-dependencies"])
    .AddCheck<RedisHealthCheck>("redis", tags: ["live-dependencies"]);

var app = builder.Build();

if (args.Contains("--migrate", StringComparer.Ordinal))
{
    await using var scope = app.Services.CreateAsyncScope();
    var dbContext = scope.ServiceProvider.GetRequiredService<OrdersDbContext>();
    await dbContext.Database.MigrateAsync();
    return;
}

app.UseExceptionHandler();
app.UseMiddleware<BuildingBlocks.CorrelationIdMiddleware>();
app.UseRateLimiter();
app.UseAuthentication();
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
app.MapHealthChecks("/health/dependencies", new HealthCheckOptions
{
    Predicate = registration => registration.Tags.Contains("live-dependencies")
});

app.MapGet("/", () => Results.Ok(new { service = "Orders.Api", instanceId }));

var orders = app.MapGroup("/orders").RequireRateLimiting(RateLimitingExtensions.OrdersPolicy);
orders.MapOrderEndpoints();
orders.MapFulfillmentEndpoints();
orders.MapCancellationEndpoints();
orders.MapReturnEndpoints();
orders.MapOrderSummaryEndpoints();
orders.MapOrderHistoryEndpoints();
app.MapGrpcService<OrderQueryGrpcService>();

await app.RunAsync();

public partial class Program;
