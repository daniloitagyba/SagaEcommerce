using BuildingBlocks;
using BuildingBlocks.WebAuthentication;
using Catalog.Service.Data;
using Catalog.Service.Endpoints;
using Catalog.Service.Health;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using MongoDB.Driver;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddSingleton(TimeProvider.System);

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
builder.Services.AddOpenApi();
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

builder.Services.AddKeycloakJwtBearer(builder.Configuration, audience: "catalog-service");
builder.Services.AddAuthorizationBuilder()
    .AddPolicy("catalog:admin", policy => policy.RequireRole("catalog:admin"));

builder.Services.AddHealthChecks()
    .AddCheck<MongoHealthCheck>("mongo", tags: ["ready"]);

var app = builder.Build();

await using (var scope = app.Services.CreateAsyncScope())
{
    var productRepository = scope.ServiceProvider.GetRequiredService<ProductRepository>();
    var categoryRepository = scope.ServiceProvider.GetRequiredService<CategoryRepository>();
    await productRepository.EnsureIndexesAsync(CancellationToken.None);
    await categoryRepository.EnsureIndexesAsync(CancellationToken.None);

    if (args.Contains("--seed", StringComparer.Ordinal))
    {
        var timeProvider = scope.ServiceProvider.GetRequiredService<TimeProvider>();
        await CatalogSeeder.SeedAsync(categoryRepository, productRepository, timeProvider.GetUtcNow(), CancellationToken.None);
        return;
    }
}

app.UseExceptionHandler();
app.UseMiddleware<CorrelationIdMiddleware>();
app.UseAuthentication();
app.UseAuthorization();

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

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
