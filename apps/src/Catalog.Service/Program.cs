using BuildingBlocks;
using Catalog.Service.Data;
using Catalog.Service.Endpoints;
using Catalog.Service.Health;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using MongoDB.Driver;

var builder = WebApplication.CreateBuilder(args);

// Milestone 73: same guard Orders.Api has carried since Milestone 69, where
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
builder.Services.AddHealthChecks()
    .AddCheck<MongoHealthCheck>("mongo", tags: ["ready"]);

var app = builder.Build();

if (args.Contains("--seed", StringComparer.Ordinal))
{
    await using var scope = app.Services.CreateAsyncScope();
    var productRepository = scope.ServiceProvider.GetRequiredService<ProductRepository>();
    var categoryRepository = scope.ServiceProvider.GetRequiredService<CategoryRepository>();
    await productRepository.EnsureIndexesAsync(CancellationToken.None);
    await categoryRepository.EnsureIndexesAsync(CancellationToken.None);
    await CatalogSeeder.SeedAsync(categoryRepository, productRepository, CancellationToken.None);
    return;
}

app.UseExceptionHandler();

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
