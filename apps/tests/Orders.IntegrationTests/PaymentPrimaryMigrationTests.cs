using BuildingBlocks;
using Microsoft.EntityFrameworkCore;
using Payments.Service.Data;
using Testcontainers.PostgreSql;

namespace Orders.IntegrationTests;

public sealed class PaymentPrimaryMigrationTests : IAsyncLifetime
{
    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder("postgres:17-alpine")
        .WithDatabase("payments_migration_test")
        .WithUsername("test_user")
        .WithPassword("test-password-not-a-secret")
        .Build();

    public Task InitializeAsync() => _postgres.StartAsync();

    public Task DisposeAsync() => _postgres.DisposeAsync().AsTask();

    [Fact]
    public async Task MigrationPreservesDuplicateHistoryAndNominatesLatestPaymentAsPrimary()
    {
        var options = new DbContextOptionsBuilder<PaymentsDbContext>()
            .UseNpgsql(_postgres.GetConnectionString())
            .Options;
        await using var dbContext = new PaymentsDbContext(options);
        await dbContext.Database.MigrateAsync("20260807131023_AddPaymentShippingPrefix");

        var orderId = Guid.NewGuid();
        var olderId = Guid.NewGuid();
        var newerId = Guid.NewGuid();
        var olderAt = DateTimeOffset.UtcNow.AddMinutes(-1);
        var newerAt = DateTimeOffset.UtcNow;
        await InsertLegacyPaymentAsync(dbContext, olderId, orderId, olderAt);
        await InsertLegacyPaymentAsync(dbContext, newerId, orderId, newerAt);

        await dbContext.Database.MigrateAsync();

        var payments = await dbContext.Payments
            .AsNoTracking()
            .Where(payment => payment.OrderId == orderId)
            .ToListAsync();
        Assert.Equal(2, payments.Count);
        Assert.Equal(newerId, Assert.Single(payments, payment => payment.IsPrimary).Id);
        Assert.Contains(payments, payment => payment.Id == olderId && !payment.IsPrimary);
    }

    private static async Task InsertLegacyPaymentAsync(
        PaymentsDbContext dbContext,
        Guid paymentId,
        Guid orderId,
        DateTimeOffset decidedAt)
    {
        DateTimeOffset? authorizationExpiresAt = null;
        string? settlementReason = null;
        await dbContext.Database.ExecuteSqlInterpolatedAsync(
            $"""
            INSERT INTO payments (
                id, order_id, customer_id, amount, currency, approved,
                decided_at, correlation_id, method, shipping_postal_prefix,
                state, authorization_expires_at, settled_at,
                settlement_reason, refunded_amount)
            VALUES (
                {paymentId}, {orderId}, {"migration-customer"}, {49.90m}, {"BRL"}, {true},
                {decidedAt}, {"migration-correlation"}, {PaymentMethods.Pix}, {"01"},
                {PaymentStates.Captured}, {authorizationExpiresAt}, {decidedAt},
                {settlementReason}, {0m})
            """);
    }
}
