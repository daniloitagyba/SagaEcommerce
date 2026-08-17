extern alias OrdersApi;

using BuildingBlocks;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Orders.Infrastructure.Data;
using Testcontainers.PostgreSql;
using Testcontainers.Redis;
using OrdersApiProgram = OrdersApi::Program;

namespace Orders.IntegrationTests;

/// <summary>Boots the real Orders.Api pipeline against real, ephemeral Postgres and Redis, with the JWT bearer scheme replaced by TestAuthHandler so tests authenticate via headers instead of a live Keycloak.</summary>
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

        Environment.SetEnvironmentVariable("ConnectionStrings__Orders", _postgres.GetConnectionString());
        Environment.SetEnvironmentVariable("Redis__ConnectionString", _redis.GetConnectionString());
        Environment.SetEnvironmentVariable("Kafka__BootstrapServers", "localhost:1");
        Environment.SetEnvironmentVariable("Kafka__OrderCreatedTopic", "orders.created.v1");
        Environment.SetEnvironmentVariable("CatalogClient__BaseUrl", "http://localhost:1");
        Environment.SetEnvironmentVariable("Authentication__Authority", "http://localhost:1/realms/test");

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
        builder.UseSetting("InstanceId", "orders-api-http-tests");

        builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<ICatalogClient>();
            services.AddSingleton<ICatalogClient, TestCatalogClient>();
            services.AddAuthentication(TestAuthHandler.SchemeName)
                .AddScheme<AuthenticationSchemeOptions, TestAuthHandler>(TestAuthHandler.SchemeName, _ => { });
        });
    }

    private sealed class TestCatalogClient : ICatalogClient
    {
        public Task<CatalogProductSnapshot?> FindBySkuAsync(string sku, CancellationToken cancellationToken) =>
            Task.FromResult<CatalogProductSnapshot?>(sku switch
            {
                "SKU-BOOK-002" => new CatalogProductSnapshot("book-002", "Test book", 49.90m, "BRL", sku, "books"),
                "SKU-ELEC-001" => new CatalogProductSnapshot("elec-001", "Test electronics", 1_500m, "BRL", sku, "electronics"),
                _ => null
            });
    }
}
