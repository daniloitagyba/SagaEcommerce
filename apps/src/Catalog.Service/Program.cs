using BuildingBlocks;
using BuildingBlocks.WebAuthentication;
using Catalog.Service.Data;
using Catalog.Service.Endpoints;
using Catalog.Service.Health;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using MongoDB.Driver;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddSingleton(TimeProvider.System);

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
builder.Logging.AddOrdersOpenTelemetryLogging("catalog-service", instanceId, builder.Environment.EnvironmentName);

builder.Services.AddOrdersObservability("catalog-service", instanceId, builder.Environment.EnvironmentName);
builder.Services.AddProblemDetails();

builder.Services.AddOptions<CatalogMongoOptions>()
    .Bind(builder.Configuration.GetSection(CatalogMongoOptions.SectionName))
    .Validate(options => !string.IsNullOrWhiteSpace(options.ConnectionString), "Mongo connection string is required.")
    .Validate(options => !string.IsNullOrWhiteSpace(options.DatabaseName), "Mongo database name is required.")
    .ValidateOnStart();

builder.Services.AddSingleton<IMongoClient>(serviceProvider =>
{
    var options = serviceProvider.GetRequiredService<Microsoft.Extensions.Options.IOptions<CatalogMongoOptions>>().Value;
    return new MongoClient(options.ConnectionString);
});
builder.Services.AddSingleton<IMongoDatabase>(serviceProvider =>
{
    var options = serviceProvider.GetRequiredService<Microsoft.Extensions.Options.IOptions<CatalogMongoOptions>>().Value;
    var client = serviceProvider.GetRequiredService<IMongoClient>();
    return client.GetDatabase(options.DatabaseName);
});

builder.Services.AddSingleton<ProductRepository>();
builder.Services.AddSingleton<CategoryRepository>();
builder.Services.AddOrdersRedis(builder.Configuration);
builder.Services.AddSingleton<BestsellersReader>();

// Catalog writes were unauthenticated - anyone who could
// reach this pod could add a product at any price, which
// OrderPricingService then trusts as the live catalog price. Same JWKS-backed
// validation as Orders.Api; catalog:admin is checked, not
// orders:write, since a shopper's checkout token should never be able to
// write a product.
builder.Services.AddKeycloakJwtBearer(builder.Configuration, audience: "catalog-service");
builder.Services.AddAuthorizationBuilder()
    .AddPolicy("catalog:admin", policy => policy.RequireRole("catalog:admin"));

builder.Services.AddHealthChecks()
    .AddCheck<MongoHealthCheck>("mongo", tags: ["ready"]);

var app = builder.Build();

// Previously only ran under --seed, so a normal deployment (migration Jobs
// run once at deploy time, the app pod never gets --seed) would serve
// traffic against a Mongo collection with none of ProductRepository's/
// CategoryRepository's indexes ever created - including the unique sku and
// slug indexes CreateAsync relies on to reject duplicates.
await using (var scope = app.Services.CreateAsyncScope())
{
    var productRepository = scope.ServiceProvider.GetRequiredService<ProductRepository>();
    var categoryRepository = scope.ServiceProvider.GetRequiredService<CategoryRepository>();
    await productRepository.EnsureIndexesAsync(CancellationToken.None);
    await categoryRepository.EnsureIndexesAsync(CancellationToken.None);

    if (args.Contains("--seed", StringComparer.Ordinal))
    {
        await CatalogSeeder.SeedAsync(categoryRepository, productRepository, CancellationToken.None);
        return;
    }
}

app.UseExceptionHandler();
app.UseAuthentication();
app.UseAuthorization();

app.MapHealthChecks("/health/live", new HealthCheckOptions
{
    Predicate = _ => false
});
app.MapHealthChecks("/health/ready", new HealthCheckOptions
{
    Predicate = registration => registration.Tags.Contains("ready")
});
app.MapGet("/", () => Results.Ok(new { service = "Catalog.Service", instanceId }));
app.MapProductEndpoints();
app.MapCategoryEndpoints();

await app.RunAsync();
