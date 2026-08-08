using BuildingBlocks;
using Confluent.Kafka;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Payments.Service;
using Payments.Service.Data;
using Payments.Service.Domain;
using Testcontainers.PostgreSql;

namespace Orders.IntegrationTests;

/// <summary>
/// Milestone 76: reproduces the race at its source, in Payments.Service
/// itself - a capture command arrives after the authorization has already
/// expired (the sweeper won the race). Before this milestone,
/// PaymentSettlementProcessor's `if (!changed)` branch logged
/// "AlreadySettled" and returned without publishing anything, so this
/// exact outcome was indistinguishable from a harmless redelivered capture
/// and invisible to the rest of the system.
/// </summary>
public sealed class PaymentSettlementProcessorTests : IAsyncLifetime
{
    private static readonly System.Text.Json.JsonSerializerOptions SerializerOptions = new(System.Text.Json.JsonSerializerDefaults.Web);

    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder("postgres:17-alpine")
        .WithDatabase("payments_test")
        .WithUsername("test_user")
        .WithPassword("test-password-not-a-secret")
        .Build();

    private ServiceProvider _serviceProvider = null!;

    public async Task InitializeAsync()
    {
        await _postgres.StartAsync();

        var services = new ServiceCollection();
        services.AddDbContext<PaymentsDbContext>(options => options.UseNpgsql(_postgres.GetConnectionString()));
        _serviceProvider = services.BuildServiceProvider();

        await using var scope = _serviceProvider.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<PaymentsDbContext>();
        await dbContext.Database.MigrateAsync();
    }

    public async Task DisposeAsync() => await _serviceProvider.DisposeAsync();

    [Fact]
    public async Task ACaptureThatArrivesAfterTheHoldAlreadyExpiredPublishesAMismatchReplyInsteadOfSilence()
    {
        var orderId = Guid.NewGuid();
        await SeedPaymentAsync(orderId, PaymentStates.Expired);
        var processor = CreateProcessor();

        var result = await processor.ProcessAsync(CaptureConsumeResult(orderId), CancellationToken.None);

        // Not Duplicate: this is not a harmless redelivery of a capture
        // that already succeeded - it's a capture that can never succeed
        // now, and the caller needs to know the difference.
        Assert.Equal(MessageProcessingResult.Processed, result);

        await using var scope = _serviceProvider.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<PaymentsDbContext>();
        var outboxMessage = await dbContext.OutboxMessages.SingleAsync();
        Assert.Equal(nameof(PaymentSettlementReplied), outboxMessage.EventType);
        var reply = System.Text.Json.JsonSerializer.Deserialize<PaymentSettlementReplied>(outboxMessage.Payload, SerializerOptions);
        Assert.Equal(PaymentStates.Expired, reply!.State);
    }

    [Fact]
    public async Task ARedeliveredCaptureOfAnAlreadyCapturedPaymentIsATrueDuplicateWithNoSecondReply()
    {
        var orderId = Guid.NewGuid();
        await SeedPaymentAsync(orderId, PaymentStates.Captured);
        var processor = CreateProcessor();

        var result = await processor.ProcessAsync(CaptureConsumeResult(orderId), CancellationToken.None);

        Assert.Equal(MessageProcessingResult.Duplicate, result);

        await using var scope = _serviceProvider.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<PaymentsDbContext>();
        Assert.Empty(dbContext.OutboxMessages);
    }

    [Fact]
    public async Task ACaptureOfAGenuinelyAuthorizedPaymentStillSucceedsNormally()
    {
        var orderId = Guid.NewGuid();
        await SeedPaymentAsync(orderId, PaymentStates.Authorized);
        var processor = CreateProcessor();

        var result = await processor.ProcessAsync(CaptureConsumeResult(orderId), CancellationToken.None);

        Assert.Equal(MessageProcessingResult.Processed, result);

        await using var scope = _serviceProvider.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<PaymentsDbContext>();
        var payment = await dbContext.Payments.SingleAsync();
        Assert.Equal(PaymentStates.Captured, payment.State);
        var outboxMessage = await dbContext.OutboxMessages.SingleAsync();
        var reply = System.Text.Json.JsonSerializer.Deserialize<PaymentSettlementReplied>(outboxMessage.Payload, SerializerOptions);
        Assert.Equal(PaymentStates.Captured, reply!.State);
    }

    private async Task SeedPaymentAsync(Guid orderId, string targetState)
    {
        var now = DateTimeOffset.UtcNow;
        var payment = Payment.Authorize(
            orderId, "settlement-test-customer", 149.90m, "BRL", PaymentMethods.Card, "01",
            approved: true, now.AddMinutes(-5), TimeSpan.FromMinutes(30), "settlement-test-correlation");

        if (targetState != PaymentStates.Authorized)
        {
            payment.TrySettleWithoutCapture(targetState, "test fixture setup", now);
        }

        await using var scope = _serviceProvider.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<PaymentsDbContext>();
        dbContext.Payments.Add(payment);
        await dbContext.SaveChangesAsync();
    }

    private PaymentSettlementProcessor CreateProcessor() =>
        new(
            _serviceProvider.GetRequiredService<IServiceScopeFactory>(),
            Options.Create(new PaymentSettlementOptions()),
            NullLogger<PaymentSettlementProcessor>.Instance);

    private static ConsumeResult<string, string> CaptureConsumeResult(Guid orderId)
    {
        var request = new PaymentCaptureRequested(orderId, "settlement-test-correlation", DateTimeOffset.UtcNow);
        return new ConsumeResult<string, string>
        {
            Topic = "payments.capture-requested.v1",
            Partition = new Partition(0),
            Offset = new Offset(0),
            Message = new Message<string, string>
            {
                Key = orderId.ToString("N"),
                Value = System.Text.Json.JsonSerializer.Serialize(request, SerializerOptions),
                Headers = new Headers()
            }
        };
    }
}
