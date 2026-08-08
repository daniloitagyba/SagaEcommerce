using System.Security.Claims;
using System.Text.Json;
using BuildingBlocks;
using Cart.Service.Data;
using Cart.Service.Endpoints;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;

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
builder.Logging.AddOrdersOpenTelemetryLogging("cart-service", instanceId, builder.Environment.EnvironmentName);

builder.Services.AddOrdersObservability("cart-service", instanceId, builder.Environment.EnvironmentName);

builder.Services.AddProblemDetails();
builder.Services.AddOrdersRedis(builder.Configuration);
builder.Services.AddOrdersResilience();
builder.Services.AddCartRedisResilience();
builder.Services.AddOptions<CartOptions>()
    .Bind(builder.Configuration.GetSection(CartOptions.SectionName))
    .Validate(options => options.TimeToLiveSeconds > 0, "Cart time-to-live must be positive.")
    .ValidateOnStart();
builder.Services.AddOptions<CatalogClientOptions>()
    .Bind(builder.Configuration.GetSection(CatalogClientOptions.SectionName))
    .Validate(options => !string.IsNullOrWhiteSpace(options.BaseUrl), "Catalog base URL is required.")
    .ValidateOnStart();

builder.Services.AddSingleton<CartStore>();
builder.Services.AddHttpClient<ICatalogClient, CatalogClient>((serviceProvider, client) =>
{
    var options = serviceProvider.GetRequiredService<Microsoft.Extensions.Options.IOptions<CatalogClientOptions>>().Value;
    client.BaseAddress = new Uri(options.BaseUrl);
    client.Timeout = TimeSpan.FromSeconds(5);
}).AddStandardResilienceHandler();

// Milestone 84: a cart used to be readable and clearable by anyone who
// could guess or enumerate its cartId - there was no owner to check
// because there was no identity at all. Requiring authentication and
// deriving the cart's key from the caller (CartEndpoints.GetCustomerId)
// removes the enumeration surface entirely rather than guarding it: there
// is no longer a client-supplied id that names someone else's cart.
var authority = builder.Configuration["Authentication:Authority"]
    ?? throw new InvalidOperationException("Authentication:Authority is required.");
builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(options =>
    {
        options.Authority = authority;
        options.Audience = "cart-service";
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
builder.Services.AddAuthorization();

builder.Services.AddHealthChecks()
    .AddCheck<RedisHealthCheck>("redis", tags: ["ready"]);

var app = builder.Build();

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
app.MapGet("/", () => Results.Ok(new { service = "Cart.Service", instanceId }));
app.MapCartEndpoints();

await app.RunAsync();
