using BuildingBlocks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Orders.Infrastructure.Data;
using Orders.Worker;
using Polly.Registry;

namespace Orders.IntegrationTests;

[Collection(PostgresCollectionDefinition.Name)]
public sealed class OrderProjectionStoreTests(PostgresFixture fixture) : IAsyncLifetime
{
    private NpgsqlDataSource _dataSource = null!;
    private ResiliencePipelineProvider<string> _pipelineProvider = null!;

    public async Task InitializeAsync()
    {
        var connectionString = await fixture.CreateSchemaAsync(nameof(OrderProjectionStoreTests));

        var options = new DbContextOptionsBuilder<OrdersDbContext>()
            .UseNpgsql(connectionString)
            .Options;
        await using (var dbContext = new OrdersDbContext(options))
        {
            await dbContext.Database.MigrateAsync();
        }

        _dataSource = NpgsqlDataSource.Create(connectionString);
        _pipelineProvider = new ServiceCollection()
            .AddOrdersResilience()
            .BuildServiceProvider()
            .GetRequiredService<ResiliencePipelineProvider<string>>();
    }

    public async Task DisposeAsync()
    {
        await _dataSource.DisposeAsync();
    }

    [Fact]
    public async Task OrderCreatedThenPaymentDecidedProducesAFullyPopulatedRow()
    {
        var store = new OrderProjectionStore(_dataSource, _pipelineProvider);
        var orderId = Guid.NewGuid();
        var createdAt = DateTimeOffset.UtcNow;

        await store.ProjectOrderCreatedAsync(orderId, "customer-a", 49.90m, "BRL", createdAt, DateTimeOffset.UtcNow, CancellationToken.None);
        await store.ProjectPaymentDecidedAsync(orderId, "Confirmed", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, CancellationToken.None);

        var summary = await ReadSummaryAsync(orderId);

        Assert.Equal("customer-a", summary.CustomerId);
        Assert.Equal(49.90m, summary.Amount);
        Assert.Equal("BRL", summary.Currency);
        Assert.Equal("Confirmed", summary.Status);
        Assert.NotNull(summary.OrderCreatedAt);
        Assert.NotNull(summary.DecidedAt);
    }

    [Fact]
    public async Task PaymentDecidedArrivingBeforeOrderCreatedStillConvergesToAFullRow()
    {
        var store = new OrderProjectionStore(_dataSource, _pipelineProvider);
        var orderId = Guid.NewGuid();

        await store.ProjectPaymentDecidedAsync(orderId, "Cancelled", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, CancellationToken.None);
        var afterDecisionOnly = await ReadSummaryAsync(orderId);
        Assert.Null(afterDecisionOnly.CustomerId);
        Assert.Equal("Cancelled", afterDecisionOnly.Status);

        await store.ProjectOrderCreatedAsync(orderId, "customer-b", 1500.00m, "BRL", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, CancellationToken.None);

        var summary = await ReadSummaryAsync(orderId);
        Assert.Equal("customer-b", summary.CustomerId);
        Assert.Equal(1500.00m, summary.Amount);
        Assert.Equal("Cancelled", summary.Status);
    }

    private async Task<(string? CustomerId, decimal? Amount, string? Currency, string Status, DateTimeOffset? OrderCreatedAt, DateTimeOffset? DecidedAt)> ReadSummaryAsync(Guid orderId)
    {
        await using var command = _dataSource.CreateCommand(
            "SELECT customer_id, amount, currency, status, order_created_at, decided_at FROM order_summaries WHERE order_id = @order_id");
        command.Parameters.AddWithValue("order_id", orderId);
        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());

        return (
            await reader.IsDBNullAsync(0) ? null : reader.GetString(0),
            await reader.IsDBNullAsync(1) ? null : reader.GetDecimal(1),
            await reader.IsDBNullAsync(2) ? null : reader.GetString(2),
            reader.GetString(3),
            await reader.IsDBNullAsync(4) ? null : await reader.GetFieldValueAsync<DateTimeOffset>(4),
            await reader.IsDBNullAsync(5) ? null : await reader.GetFieldValueAsync<DateTimeOffset>(5));
    }
}
