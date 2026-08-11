extern alias OrdersApi;

using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Orders.Infrastructure.Data;
using Testcontainers.PostgreSql;
using Testcontainers.Redis;
using OrdersApiProgram = OrdersApi::Program;

namespace Orders.IntegrationTests;

/// <summary>
/// Boots the real Orders.Api pipeline (Program.cs's ValidateOnBuild
/// requires every registered service to actually construct, not just
/// Program.cs's own logic) against real, ephemeral Postgres and Redis -
/// EF migrations run for real, IConnectionMultiplexer connects for real
/// (StackExchange.Redis's ConnectionMultiplexer.Connect blocks at
/// construction, which ValidateOnBuild forces at startup, not first use).
/// Kafka's producers/admin client don't need the same treatment: Confluent.
/// Kafka builds them lazily and only actually reaches the broker on first
/// produce, so a syntactically-valid-but-unreachable bootstrap servers
/// value is enough for the app to start - none of the HTTP-level routing/
/// auth/model-binding behavior these tests exercise depends on a message
/// actually being produced. The real JWT bearer scheme is replaced with
/// TestAuthHandler, so tests authenticate via headers instead of needing a
/// live Keycloak.
/// </summary>
public sealed class OrdersApiFactory : WebApplicationFactory<OrdersApiProgram>, IAsyncLifetime
{
    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder("postgres:17-alpine")
        .WithDatabase("orders_http_test")
        .WithUsername("test_user")
        .WithPassword("test-password-not-a-secret")
        .Build();

    private readonly RedisContainer _redis = new RedisBuilder("redis:7.4-alpine").Build();

    public async Task InitializeAsync()
    {
        await Task.WhenAll(_postgres.StartAsync(), _redis.StartAsync());

        // Program.cs reads builder.Configuration.GetConnectionString("Orders")
        // into a plain local variable before builder.Build() is ever called,
        // and WebApplicationFactory only gets to layer ConfigureAppConfiguration
        // overrides onto the builder at the Build() interception point - by
        // then that local variable already has appsettings.json's value baked
        // in. Environment variables are read by WebApplicationBuilder.CreateBuilder()
        // itself, which runs before any of Program.cs's own code, so setting
        // them here (before the host is first built, below) is early enough.
        Environment.SetEnvironmentVariable("ConnectionStrings__Orders", _postgres.GetConnectionString());
        Environment.SetEnvironmentVariable("Redis__ConnectionString", _redis.GetConnectionString());
        Environment.SetEnvironmentVariable("Kafka__BootstrapServers", "localhost:1");
        Environment.SetEnvironmentVariable("Kafka__OrderCreatedTopic", "orders.created.v1");
        Environment.SetEnvironmentVariable("CatalogClient__BaseUrl", "http://localhost:1");
        Environment.SetEnvironmentVariable("Authentication__Authority", "http://localhost:1/realms/test");

        // Forces the host to actually build (ValidateOnBuild) before the
        // first test runs, rather than lazily on the first HTTP request -
        // a DI graph problem should fail InitializeAsync, not a random test.
        using var scope = Services.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<OrdersDbContext>();
        await dbContext.Database.MigrateAsync();
    }

    async Task IAsyncLifetime.DisposeAsync()
    {
        await Task.WhenAll(_postgres.DisposeAsync().AsTask(), _redis.DisposeAsync().AsTask());
        await base.DisposeAsync();
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        // All connection settings are supplied via environment variables in
        // InitializeAsync, above - see that method's comment for why
        // ConfigureAppConfiguration doesn't reach Program.cs's own
        // GetConnectionString("Orders") call in time.
        builder.UseSetting("InstanceId", "orders-api-http-tests");

        builder.ConfigureTestServices(services =>
        {
            services.AddAuthentication(TestAuthHandler.SchemeName)
                .AddScheme<AuthenticationSchemeOptions, TestAuthHandler>(TestAuthHandler.SchemeName, _ => { });
        });
    }
}
