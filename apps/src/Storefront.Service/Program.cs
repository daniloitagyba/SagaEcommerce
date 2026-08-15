using BuildingBlocks;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;
using Storefront.Service;

var builder = WebApplication.CreateBuilder(args);

// Same guard Orders.Api already carries, where
// one unregistered IProducer took the whole outbox down while the service
// went on reporting healthy - a background loop cannot fail loudly on its
// own, so the failure has to happen at startup instead.
builder.Host.UseDefaultServiceProvider(options =>
{
    options.ValidateOnBuild = true;
    options.ValidateScopes = true;
});
var instanceId = builder.Configuration["InstanceId"] ?? Environment.MachineName;

builder.Logging.ClearProviders();
builder.Logging.AddJsonConsole(options =>
{
    options.IncludeScopes = true;
    options.UseUtcTimestamp = true;
});
builder.Logging.AddOrdersOpenTelemetryLogging("storefront-service", instanceId, builder.Environment.EnvironmentName);

builder.Services.AddOrdersObservability("storefront-service", instanceId, builder.Environment.EnvironmentName);
builder.Services.AddProblemDetails();

builder.Services.AddOptions<CatalogProxyOptions>()
    .Bind(builder.Configuration.GetSection(CatalogProxyOptions.SectionName))
    .Validate(options => !string.IsNullOrWhiteSpace(options.BaseUrl), "Catalog base URL is required.")
    .ValidateOnStart();
builder.Services.AddOptions<CartProxyOptions>()
    .Bind(builder.Configuration.GetSection(CartProxyOptions.SectionName))
    .Validate(options => !string.IsNullOrWhiteSpace(options.BaseUrl), "Cart base URL is required.")
    .ValidateOnStart();
builder.Services.AddOptions<OrdersProxyOptions>()
    .Bind(builder.Configuration.GetSection(OrdersProxyOptions.SectionName))
    .Validate(options => !string.IsNullOrWhiteSpace(options.BaseUrl), "Orders base URL is required.")
    .ValidateOnStart();
builder.Services.AddOptions<InventoryProxyOptions>()
    .Bind(builder.Configuration.GetSection(InventoryProxyOptions.SectionName))
    .Validate(options => !string.IsNullOrWhiteSpace(options.BaseUrl), "Inventory base URL is required.")
    .ValidateOnStart();
builder.Services.AddOptions<ProductSummaryOptions>()
    .Bind(builder.Configuration.GetSection(ProductSummaryOptions.SectionName))
    .Validate(options => options.HedgeDelayMilliseconds >= 0, "Hedge delay cannot be negative.")
    .ValidateOnStart();
// catalog/cart/inventory back browse-time display (product listings,
// cart contents, stock) - a failure there is recoverable by the shopper
// retrying, unlike orders below.
builder.Services.AddHttpClient("catalog", (serviceProvider, client) =>
{
    client.BaseAddress = new Uri(serviceProvider.GetRequiredService<IOptions<CatalogProxyOptions>>().Value.BaseUrl);
}).AddBestEffortHttpResilience();

builder.Services.AddHttpClient("cart", (serviceProvider, client) =>
{
    client.BaseAddress = new Uri(serviceProvider.GetRequiredService<IOptions<CartProxyOptions>>().Value.BaseUrl);
}).AddBestEffortHttpResilience();

// Checkout's own forward - the one client on this BFF's genuinely
// critical path.
builder.Services.AddHttpClient("orders", (serviceProvider, client) =>
{
    client.BaseAddress = new Uri(serviceProvider.GetRequiredService<IOptions<OrdersProxyOptions>>().Value.BaseUrl);
}).AddCriticalHttpResilience();

builder.Services.AddHttpClient("inventory", (serviceProvider, client) =>
{
    client.BaseAddress = new Uri(serviceProvider.GetRequiredService<IOptions<InventoryProxyOptions>>().Value.BaseUrl);
})
// The tail-latency benchmark routes this client through a
// Toxiproxy latency toxic with a toxicity probability - Toxiproxy applies
// that probability per proxied TCP connection, not per HTTP request, so a
// pooled/reused keep-alive connection would be either always or never
// toxic for its entire lifetime instead of the intended "N% of requests
// are slow." Forcing a fresh connection per request makes the toxicity
// roll happen per request, matching the fault this benchmark models.
.ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler { PooledConnectionLifetime = TimeSpan.FromMilliseconds(1) })
.AddBestEffortHttpResilience();

builder.Services.AddHealthChecks();

var app = builder.Build();

app.UseExceptionHandler();

app.MapHealthChecks("/health/live", new HealthCheckOptions { Predicate = _ => false });
app.MapHealthChecks("/health/ready", new HealthCheckOptions { Predicate = _ => false });

app.MapProxyEndpoints();
app.MapStorefrontEndpoints();

app.UseDefaultFiles();
app.UseStaticFiles();
app.MapFallbackToFile("index.html");

await app.RunAsync();
