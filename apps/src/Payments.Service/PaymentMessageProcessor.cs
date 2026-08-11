using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Avro.Generic;
using BuildingBlocks;
using Confluent.Kafka;
using Confluent.SchemaRegistry;
using Confluent.SchemaRegistry.Serdes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Payments.Service.Data;
using Payments.Service.Domain;
using Payments.Service.Messaging;

namespace Payments.Service;

public enum MessageProcessingResult
{
    Processed,
    Duplicate
}

public sealed class InvalidOrderMessageException(string message, Exception? innerException = null)
    : Exception(message, innerException);

public sealed class PaymentMessageProcessor(
    IServiceScopeFactory scopeFactory,
    ISchemaRegistryClient schemaRegistryClient,
    IOptions<PaymentsKafkaOptions> kafkaOptions,
    ILogger<PaymentMessageProcessor> logger)
{
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);
    private readonly PaymentsKafkaOptions _kafkaOptions = kafkaOptions.Value;
    private readonly AvroDeserializer<GenericRecord> _avroDeserializer = new(schemaRegistryClient);

    public async Task<MessageProcessingResult> ProcessAsync(
        ConsumeResult<string, byte[]> consumeResult,
        CancellationToken cancellationToken)
    {
        var orderCreated = await DeserializeAndValidateAsync(consumeResult, cancellationToken);
        var correlationId = GetHeader(consumeResult.Message.Headers, MessagingHeaders.CorrelationId)
            ?? orderCreated.CorrelationId;
        var traceParent = GetHeader(consumeResult.Message.Headers, MessagingHeaders.TraceParent);
        var traceState = GetHeader(consumeResult.Message.Headers, MessagingHeaders.TraceState);

        using var activity = OrdersTelemetry.StartActivity(
            "payments.decide",
            ActivityKind.Consumer,
            traceParent,
            traceState);
        activity?.SetTag("messaging.system", "kafka");
        activity?.SetTag("messaging.destination.name", consumeResult.Topic);
        activity?.SetTag("messaging.operation.type", "process");
        activity?.SetTag("messaging.message.id", orderCreated.EventId);
        activity?.SetTag("order.id", orderCreated.OrderId);
        activity?.SetTag("correlation.id", correlationId);

        using var scope = logger.BeginScope(new Dictionary<string, object?>
        {
            ["CorrelationId"] = correlationId,
            ["EventId"] = orderCreated.EventId,
            ["OrderId"] = orderCreated.OrderId,
            ["TraceId"] = activity?.TraceId.ToString() ?? string.Empty
        });

        await using var serviceScope = scopeFactory.CreateAsyncScope();
        var dbContext = serviceScope.ServiceProvider.GetRequiredService<PaymentsDbContext>();

        await using var transaction = await dbContext.Database.BeginTransactionAsync(cancellationToken);

        var processedAt = DateTimeOffset.UtcNow;
        var insertedRows = await dbContext.Database.ExecuteSqlInterpolatedAsync(
            $"""
            INSERT INTO inbox_messages (consumer_name, event_id, topic, partition, "offset", correlation_id, processed_at)
            VALUES ({_kafkaOptions.ConsumerGroup}, {orderCreated.EventId}, {consumeResult.Topic}, {consumeResult.Partition.Value}, {consumeResult.Offset.Value}, {correlationId}, {processedAt})
            ON CONFLICT (consumer_name, event_id) DO NOTHING
            """,
            cancellationToken);

        if (insertedRows == 0)
        {
            await transaction.RollbackAsync(cancellationToken);
            OrdersTelemetry.RecordProcessed("duplicate");
            OrdersTelemetry.RecordInboxDuplicate(_kafkaOptions.ConsumerGroup);
            PaymentsLog.Duplicate(logger, orderCreated.EventId, _kafkaOptions.ConsumerGroup);
            return MessageProcessingResult.Duplicate;
        }

        var decisionCoordinator = serviceScope.ServiceProvider.GetRequiredService<PaymentDecisionCoordinator>();
        var decision = await decisionCoordinator.GetOrCreateAsync(
            new PaymentDecisionInput(
                orderCreated.OrderId,
                orderCreated.CustomerId,
                orderCreated.Amount,
                orderCreated.Currency,
                orderCreated.PaymentMethod,
                orderCreated.ShippingPostalPrefix,
                correlationId),
            processedAt,
            cancellationToken);
        var payment = decision.Payment;
        var approved = payment.Approved;

        var paymentDecided = new PaymentDecided(
            Guid.NewGuid(),
            orderCreated.OrderId,
            payment.Id,
            approved,
            payment.Amount,
            payment.Currency,
            processedAt,
            correlationId);
        var outboxMessage = OutboxMessage.Create(
            paymentDecided.EventId,
            nameof(PaymentDecided),
            JsonSerializer.Serialize(paymentDecided, SerializerOptions),
            processedAt,
            correlationId,
            Activity.Current?.Id,
            Activity.Current?.TraceStateString);

        dbContext.OutboxMessages.Add(outboxMessage);
        await dbContext.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        activity?.SetTag("payment.approved", approved);
        activity?.SetTag("payment.decision_created", decision.Created);
        activity?.SetTag("payment.risk_score", decision.Assessment?.Score);
        OrdersTelemetry.RecordProcessed("success");
        OrdersTelemetry.RecordPaymentDecided(approved);
        LogDecision(logger, orderCreated.OrderId, payment, decision, correlationId);
        return MessageProcessingResult.Processed;
    }

    private static void LogDecision(
        ILogger logger,
        Guid orderId,
        Payment payment,
        PaymentDecisionResult decision,
        string correlationId)
    {
        if (decision.Assessment is { } assessment)
        {
            PaymentsLog.DecidedWithRisk(
                logger, orderId, payment.Id, payment.Approved,
                assessment.Score, assessment.ReasonSummary, correlationId);
            return;
        }

        PaymentsLog.ReusedDecision(logger, orderId, payment.Id, correlationId);
    }

    private async Task<OrderCreated> DeserializeAndValidateAsync(
        ConsumeResult<string, byte[]> consumeResult,
        CancellationToken cancellationToken)
    {
        OrderCreated orderCreated;
        try
        {
            var context = new SerializationContext(MessageComponentType.Value, consumeResult.Topic, consumeResult.Message.Headers);
            var record = await _avroDeserializer.DeserializeAsync(consumeResult.Message.Value, isNull: false, context)
                .WaitAsync(cancellationToken);
            orderCreated = OrderCreatedAvroSchema.FromGenericRecord(record);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            throw new InvalidOrderMessageException("The Kafka message is not a valid OrderCreated event.", exception);
        }

        if (orderCreated.EventId == Guid.Empty || orderCreated.OrderId == Guid.Empty)
        {
            throw new InvalidOrderMessageException("The OrderCreated event and order identifiers are required.");
        }

        // Accept every schema version this consumer can
        // actually read, not just the newest. Pinning to one exact version
        // is what turns a backward-compatible schema change into a
        // rolling-deploy outage - during the rollout both v1 and v2
        // messages are genuinely on the topic at the same time.
        if (!OrderCreatedSchemaVersions.IsSupported(orderCreated.SchemaVersion))
        {
            throw new InvalidOrderMessageException($"Unsupported OrderCreated schema version {orderCreated.SchemaVersion}.");
        }

        return orderCreated;
    }

    private static string? GetHeader(Headers headers, string key)
    {
        var header = headers.LastOrDefault(item => string.Equals(item.Key, key, StringComparison.Ordinal));
        return header is null ? null : Encoding.UTF8.GetString(header.GetValueBytes());
    }
}

public sealed partial class PaymentsLog
{
    [LoggerMessage(EventId = 5002, Level = LogLevel.Information, Message = "Decided payment {PaymentId} for order {OrderId}: approved={Approved} with correlation {CorrelationId}")]
    public static partial void Decided(ILogger logger, Guid orderId, Guid paymentId, bool approved, string correlationId);

    [LoggerMessage(EventId = 5005, Level = LogLevel.Information, Message = "Skipped duplicate event {EventId} for consumer {ConsumerName}")]
    public static partial void Duplicate(ILogger logger, Guid eventId, string consumerName);

    [LoggerMessage(EventId = 5006, Level = LogLevel.Information, Message = "Decided payment {PaymentId} for order {OrderId}: approved={Approved} riskScore={RiskScore} signals=[{RiskSignals}] with correlation {CorrelationId}")]
    public static partial void DecidedWithRisk(ILogger logger, Guid orderId, Guid paymentId, bool approved, int riskScore, string riskSignals, string correlationId);

    [LoggerMessage(EventId = 5007, Level = LogLevel.Information, Message = "Reused primary payment {PaymentId} for order {OrderId} instead of creating a second decision (correlation {CorrelationId})")]
    public static partial void ReusedDecision(ILogger logger, Guid orderId, Guid paymentId, string correlationId);
}
