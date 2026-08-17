using BuildingBlocks;
using Confluent.Kafka;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Payments.Service;
using Payments.Service.Data;
using Payments.Service.Domain;
using Polly.Registry;

namespace Orders.IntegrationTests;

/// <summary>Reproduces the race at its source in Payments.Service: a capture command arrives after the authorization has already expired. Before reconciliation, this outcome was indistinguishable from a harmless redelivered capture.</summary>
[Collection(PostgresCollectionDefinition.Name)]
public sealed class PaymentSettlementProcessorTests(PostgresFixture fixture) : IAsyncLifetime
{
    private static readonly System.Text.Json.JsonSerializerOptions SerializerOptions = new(System.Text.Json.JsonSerializerDefaults.Web);

    private ServiceProvider _serviceProvider = null!;

    public async Task InitializeAsync()
    {
        var connectionString = await fixture.CreateSchemaAsync(nameof(PaymentSettlementProcessorTests));

        var services = new ServiceCollection();
        services.AddDbContext<PaymentsDbContext>(options => options.UseNpgsql(connectionString));
        services.AddOrdersResilience();
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

        Assert.Equal(MessageProcessingResult.Processed, result);

        await using var scope = _serviceProvider.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<PaymentsDbContext>();
        var outboxMessage = await dbContext.OutboxMessages.SingleAsync();
        Assert.Equal(nameof(PaymentSettlementReplied), outboxMessage.EventType);
        var reply = System.Text.Json.JsonSerializer.Deserialize<PaymentSettlementReplied>(outboxMessage.Payload, SerializerOptions);
        Assert.Equal(PaymentStates.Expired, reply!.State);
        Assert.True(reply.RequiresReconciliation);
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
        Assert.False(reply.RequiresReconciliation);
    }

    [Fact]
    public async Task CancellingAnOrderWithACapturedPixPaymentRefundsItInsteadOfLoggingAHarmlessNoOp()
    {
        var orderId = Guid.NewGuid();
        await SeedApprovedPaymentAsync(orderId, PaymentMethods.Pix, PaymentStates.Captured);
        var processor = CreateProcessor();

        var result = await processor.ProcessAsync(CancellationConsumeResult(orderId), CancellationToken.None);

        Assert.Equal(MessageProcessingResult.Processed, result);

        await using var scope = _serviceProvider.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<PaymentsDbContext>();
        var payment = await dbContext.Payments.SingleAsync();
        Assert.Equal(PaymentStates.Refunded, payment.State);
        Assert.Equal(payment.Amount, payment.RefundedAmount);

        var outboxMessage = await dbContext.OutboxMessages.SingleAsync();
        var reply = System.Text.Json.JsonSerializer.Deserialize<PaymentSettlementReplied>(outboxMessage.Payload, SerializerOptions);
        Assert.Equal(PaymentStates.Refunded, reply!.State);
    }

    [Fact]
    public async Task CancellingAnOrderWithAnAuthorizedCardVoidsTheHold()
    {
        var orderId = Guid.NewGuid();
        await SeedPaymentAsync(orderId, PaymentStates.Authorized);
        var processor = CreateProcessor();

        var result = await processor.ProcessAsync(CancellationConsumeResult(orderId), CancellationToken.None);

        Assert.Equal(MessageProcessingResult.Processed, result);

        await using var scope = _serviceProvider.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<PaymentsDbContext>();
        var payment = await dbContext.Payments.SingleAsync();
        Assert.Equal(PaymentStates.Voided, payment.State);
    }

    [Fact]
    public async Task CancellingAnOrderWithADeclinedPaymentIsDroppedSilentlyNotReportedAsAMismatch()
    {
        var orderId = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;
        var payment = Payment.Authorize(
            orderId, "settlement-test-customer", 149.90m, "BRL", PaymentMethods.Card, "01",
            approved: false, now, TimeSpan.FromMinutes(30), "settlement-test-correlation");

        await using (var seedScope = _serviceProvider.CreateAsyncScope())
        {
            var seedDbContext = seedScope.ServiceProvider.GetRequiredService<PaymentsDbContext>();
            seedDbContext.Payments.Add(payment);
            await seedDbContext.SaveChangesAsync();
        }

        var processor = CreateProcessor();
        var result = await processor.ProcessAsync(CancellationConsumeResult(orderId), CancellationToken.None);

        Assert.Equal(MessageProcessingResult.Duplicate, result);

        await using var scope = _serviceProvider.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<PaymentsDbContext>();
        Assert.Empty(dbContext.OutboxMessages);
    }

    [Fact]
    public async Task RedeliveringTheSamePartialRefundDoesNotRefundTwice()
    {
        var orderId = Guid.NewGuid();
        var returnId = Guid.NewGuid();
        await SeedApprovedPaymentAsync(orderId, PaymentMethods.Pix, PaymentStates.Captured);
        var processor = CreateProcessor();
        var refund = RefundConsumeResult(orderId, returnId, 25m);

        var first = await processor.ProcessAsync(refund, CancellationToken.None);
        var replay = await processor.ProcessAsync(refund, CancellationToken.None);

        Assert.Equal(MessageProcessingResult.Processed, first);
        Assert.Equal(MessageProcessingResult.Duplicate, replay);

        await using var scope = _serviceProvider.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<PaymentsDbContext>();
        var payment = await dbContext.Payments.SingleAsync();
        Assert.Equal(25m, payment.RefundedAmount);
        Assert.Single(await dbContext.OutboxMessages.ToListAsync());
        Assert.Single(await dbContext.Set<InboxRecord>().ToListAsync());
    }

    [Fact]
    public async Task ConcurrentCaptureAndCancellationCannotOverwriteEachOther()
    {
        var orderId = Guid.NewGuid();
        await SeedPaymentAsync(orderId, PaymentStates.Authorized);
        var processor = CreateProcessor();

        await Task.WhenAll(
            processor.ProcessAsync(CaptureConsumeResult(orderId), CancellationToken.None),
            processor.ProcessAsync(CancellationConsumeResult(orderId), CancellationToken.None));

        await using var scope = _serviceProvider.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<PaymentsDbContext>();
        var payment = await dbContext.Payments.SingleAsync();
        Assert.Contains(payment.State, new[] { PaymentStates.Voided, PaymentStates.Refunded });
        Assert.Equal(2, await dbContext.OutboxMessages.CountAsync());
    }

    private async Task SeedApprovedPaymentAsync(Guid orderId, string method, string targetState)
    {
        var now = DateTimeOffset.UtcNow;
        var payment = Payment.Authorize(
            orderId, "settlement-test-customer", 149.90m, "BRL", method, "01",
            approved: true, now.AddMinutes(-5), TimeSpan.FromMinutes(30), "settlement-test-correlation");

        if (targetState != payment.State)
        {
            payment.TrySettleWithoutCapture(targetState, "test fixture setup", now);
        }

        await using var scope = _serviceProvider.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<PaymentsDbContext>();
        dbContext.Payments.Add(payment);
        await dbContext.SaveChangesAsync();
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
            NullLogger<PaymentSettlementProcessor>.Instance,
            _serviceProvider.GetRequiredService<ResiliencePipelineProvider<string>>());

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

    private static ConsumeResult<string, string> CancellationConsumeResult(Guid orderId)
    {
        var request = new PaymentCancellationRequested(orderId, "order cancelled", "settlement-test-correlation", DateTimeOffset.UtcNow);
        return new ConsumeResult<string, string>
        {
            Topic = "payments.cancellation-requested.v1",
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

    private static ConsumeResult<string, string> RefundConsumeResult(
        Guid orderId,
        Guid returnId,
        decimal amount)
    {
        var request = new PaymentRefundRequested(
            orderId,
            returnId,
            amount,
            "BRL",
            "returned item",
            "settlement-test-correlation",
            DateTimeOffset.UtcNow);
        return new ConsumeResult<string, string>
        {
            Topic = "payments.refund-requested.v1",
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
