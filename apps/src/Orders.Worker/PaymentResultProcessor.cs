using System.Diagnostics;
using System.Text;
using System.Text.Json;
using BuildingBlocks;
using Confluent.Kafka;
using Microsoft.Extensions.Options;

namespace Orders.Worker;

public sealed class InvalidPaymentResultMessageException(string message, Exception? innerException = null)
    : Exception(message, innerException);

public sealed class PaymentResultProcessor(
    InboxStore inboxStore,
    OrderStatusStore orderStatusStore,
    IOrderCacheInvalidator cacheInvalidator,
    IOptions<PaymentResultKafkaOptions> options,
    ILogger<PaymentResultProcessor> logger)
{
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);
    private readonly PaymentResultKafkaOptions _options = options.Value;

    public async Task<MessageProcessingResult> ProcessAsync(
        ConsumeResult<string, string> consumeResult,
        CancellationToken cancellationToken)
    {
        var paymentDecided = DeserializeAndValidate(consumeResult.Message.Value);
        var correlationId = GetHeader(consumeResult.Message.Headers, MessagingHeaders.CorrelationId)
            ?? paymentDecided.CorrelationId;
        var traceParent = GetHeader(consumeResult.Message.Headers, MessagingHeaders.TraceParent);
        var traceState = GetHeader(consumeResult.Message.Headers, MessagingHeaders.TraceState);

        using var activity = OrdersTelemetry.StartActivity(
            "orders.payment_result.process",
            ActivityKind.Consumer,
            traceParent,
            traceState);
        activity?.SetTag("messaging.system", "kafka");
        activity?.SetTag("messaging.destination.name", consumeResult.Topic);
        activity?.SetTag("messaging.operation.type", "process");
        activity?.SetTag("messaging.message.id", paymentDecided.EventId);
        activity?.SetTag("order.id", paymentDecided.OrderId);
        activity?.SetTag("payment.approved", paymentDecided.Approved);
        activity?.SetTag("correlation.id", correlationId);

        using var scope = logger.BeginScope(new Dictionary<string, object?>
        {
            ["CorrelationId"] = correlationId,
            ["EventId"] = paymentDecided.EventId,
            ["OrderId"] = paymentDecided.OrderId,
            ["TraceId"] = activity?.TraceId.ToString() ?? string.Empty
        });

        var inserted = await inboxStore.TryRecordAsync(
            _options.ConsumerGroup,
            paymentDecided.EventId,
            consumeResult.Topic,
            consumeResult.Partition.Value,
            consumeResult.Offset.Value,
            correlationId,
            DateTimeOffset.UtcNow,
            cancellationToken);

        if (!inserted)
        {
            OrdersTelemetry.RecordProcessed("duplicate");
            OrdersTelemetry.RecordInboxDuplicate(_options.ConsumerGroup);
            WorkerLog.Duplicate(logger, paymentDecided.EventId, _options.ConsumerGroup);
            return MessageProcessingResult.Duplicate;
        }

        if (paymentDecided.Approved)
        {
            await orderStatusStore.TryConfirmAsync(paymentDecided.OrderId, correlationId, cancellationToken);
        }
        else
        {
            await orderStatusStore.TryCancelAsync(paymentDecided.OrderId, correlationId, cancellationToken);
        }

        await cacheInvalidator.InvalidateAsync(paymentDecided.OrderId, cancellationToken);

        OrdersTelemetry.RecordProcessed("success");
        WorkerLog.Processed(logger, paymentDecided.OrderId, paymentDecided.EventId, correlationId);
        return MessageProcessingResult.Processed;
    }

    private static PaymentDecided DeserializeAndValidate(string payload)
    {
        PaymentDecided paymentDecided;
        try
        {
            paymentDecided = JsonSerializer.Deserialize<PaymentDecided>(payload, SerializerOptions)
                ?? throw new JsonException("The Kafka message did not contain a PaymentDecided event.");
        }
        catch (JsonException exception)
        {
            throw new InvalidPaymentResultMessageException("The Kafka message is not a valid PaymentDecided event.", exception);
        }

        if (paymentDecided.EventId == Guid.Empty || paymentDecided.OrderId == Guid.Empty)
        {
            throw new InvalidPaymentResultMessageException("The PaymentDecided event and order identifiers are required.");
        }

        if (paymentDecided.SchemaVersion != 1)
        {
            throw new InvalidPaymentResultMessageException($"Unsupported PaymentDecided schema version {paymentDecided.SchemaVersion}.");
        }

        return paymentDecided;
    }

    private static string? GetHeader(Headers headers, string key)
    {
        var header = headers.LastOrDefault(item => string.Equals(item.Key, key, StringComparison.Ordinal));
        return header is null ? null : Encoding.UTF8.GetString(header.GetValueBytes());
    }
}
