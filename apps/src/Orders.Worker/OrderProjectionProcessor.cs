using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Avro.Generic;
using BuildingBlocks;
using Confluent.Kafka;
using Confluent.SchemaRegistry;
using Confluent.SchemaRegistry.Serdes;
using Microsoft.Extensions.Options;

namespace Orders.Worker;

public sealed class InvalidProjectionMessageException(string message, Exception? innerException = null)
    : Exception(message, innerException);

public sealed class OrderProjectionProcessor(
    InboxStore inboxStore,
    OrderProjectionStore projectionStore,
    ISchemaRegistryClient schemaRegistryClient,
    IOptions<OrderProjectionOptions> options,
    ILogger<OrderProjectionProcessor> logger,
    TimeProvider? timeProvider = null)
{
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);
    private readonly OrderProjectionOptions _options = options.Value;
    private readonly AvroDeserializer<GenericRecord> _avroDeserializer = new(schemaRegistryClient);
    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;

    public async Task<MessageProcessingResult> ProcessAsync(
        ConsumeResult<string, byte[]> consumeResult,
        CancellationToken cancellationToken)
    {
        if (consumeResult.Topic == _options.OrderCreatedTopic)
        {
            return await ProcessOrderCreatedAsync(consumeResult, cancellationToken);
        }

        if (consumeResult.Topic == _options.PaymentResultTopic)
        {
            return await ProcessPaymentDecidedAsync(consumeResult, cancellationToken);
        }

        if (consumeResult.Topic == _options.OrderStatusChangedTopic)
        {
            return await ProcessOrderStatusChangedAsync(consumeResult, cancellationToken);
        }

        throw new InvalidProjectionMessageException($"Unexpected topic '{consumeResult.Topic}'.");
    }

    private async Task<MessageProcessingResult> ProcessOrderCreatedAsync(
        ConsumeResult<string, byte[]> consumeResult,
        CancellationToken cancellationToken)
    {
        var orderCreated = await DeserializeAvroAsync(consumeResult, "OrderCreated event", cancellationToken);
        if (orderCreated.EventId == Guid.Empty || orderCreated.OrderId == Guid.Empty)
        {
            throw new InvalidProjectionMessageException("The OrderCreated event and order identifiers are required.");
        }

        var correlationId = GetHeader(consumeResult.Message.Headers, MessagingHeaders.CorrelationId) ?? orderCreated.CorrelationId;
        using var activity = StartActivity(consumeResult, correlationId, orderCreated.EventId, orderCreated.OrderId);

        var inserted = await inboxStore.TryRecordAsync(
            _options.ConsumerGroup,
            orderCreated.EventId,
            consumeResult.Topic,
            consumeResult.Partition.Value,
            consumeResult.Offset.Value,
            correlationId,
            _timeProvider.GetUtcNow(),
            cancellationToken);

        if (!inserted)
        {
            OrdersTelemetry.RecordProcessed("duplicate");
            WorkerLog.Duplicate(logger, orderCreated.EventId, _options.ConsumerGroup);
            return MessageProcessingResult.Duplicate;
        }

        var projectedAt = _timeProvider.GetUtcNow();
        await projectionStore.ProjectOrderCreatedAsync(
            orderCreated.OrderId,
            orderCreated.CustomerId,
            orderCreated.Amount,
            orderCreated.Currency,
            orderCreated.OccurredAt,
            projectedAt,
            cancellationToken);

        OrdersTelemetry.RecordProcessed("success");
        OrdersTelemetry.RecordProjectionLag(nameof(OrderCreated), projectedAt - orderCreated.OccurredAt);
        return MessageProcessingResult.Processed;
    }

    private async Task<MessageProcessingResult> ProcessPaymentDecidedAsync(
        ConsumeResult<string, byte[]> consumeResult,
        CancellationToken cancellationToken)
    {
        var paymentDecided = DeserializeJson<PaymentDecided>(consumeResult.Message.Value, "PaymentDecided event");
        if (paymentDecided.EventId == Guid.Empty || paymentDecided.OrderId == Guid.Empty)
        {
            throw new InvalidProjectionMessageException("The PaymentDecided event and order identifiers are required.");
        }

        var correlationId = GetHeader(consumeResult.Message.Headers, MessagingHeaders.CorrelationId) ?? paymentDecided.CorrelationId;
        using var activity = StartActivity(consumeResult, correlationId, paymentDecided.EventId, paymentDecided.OrderId);

        var inserted = await inboxStore.TryRecordAsync(
            _options.ConsumerGroup,
            paymentDecided.EventId,
            consumeResult.Topic,
            consumeResult.Partition.Value,
            consumeResult.Offset.Value,
            correlationId,
            _timeProvider.GetUtcNow(),
            cancellationToken);

        if (!inserted)
        {
            OrdersTelemetry.RecordProcessed("duplicate");
            WorkerLog.Duplicate(logger, paymentDecided.EventId, _options.ConsumerGroup);
            return MessageProcessingResult.Duplicate;
        }

        var projectedAt = _timeProvider.GetUtcNow();
        var status = paymentDecided.Approved ? "Confirmed" : "Cancelled";
        await projectionStore.ProjectPaymentDecidedAsync(
            paymentDecided.OrderId,
            status,
            paymentDecided.OccurredAt,
            projectedAt,
            cancellationToken);

        OrdersTelemetry.RecordProcessed("success");
        OrdersTelemetry.RecordProjectionLag(nameof(PaymentDecided), projectedAt - paymentDecided.OccurredAt);
        return MessageProcessingResult.Processed;
    }

    private async Task<MessageProcessingResult> ProcessOrderStatusChangedAsync(
        ConsumeResult<string, byte[]> consumeResult,
        CancellationToken cancellationToken)
    {
        var statusChanged = DeserializeJson<OrderStatusChanged>(consumeResult.Message.Value, "OrderStatusChanged event");
        if (statusChanged.EventId == Guid.Empty || statusChanged.OrderId == Guid.Empty)
        {
            throw new InvalidProjectionMessageException("The OrderStatusChanged event and order identifiers are required.");
        }

        var correlationId = GetHeader(consumeResult.Message.Headers, MessagingHeaders.CorrelationId) ?? statusChanged.CorrelationId;
        using var activity = StartActivity(consumeResult, correlationId, statusChanged.EventId, statusChanged.OrderId);

        var inserted = await inboxStore.TryRecordAsync(
            _options.ConsumerGroup,
            statusChanged.EventId,
            consumeResult.Topic,
            consumeResult.Partition.Value,
            consumeResult.Offset.Value,
            correlationId,
            _timeProvider.GetUtcNow(),
            cancellationToken);

        if (!inserted)
        {
            OrdersTelemetry.RecordProcessed("duplicate");
            WorkerLog.Duplicate(logger, statusChanged.EventId, _options.ConsumerGroup);
            return MessageProcessingResult.Duplicate;
        }

        var projectedAt = _timeProvider.GetUtcNow();
        await projectionStore.ProjectPaymentDecidedAsync(
            statusChanged.OrderId,
            statusChanged.Status,
            statusChanged.OccurredAt,
            projectedAt,
            cancellationToken,
            statusChanged.Version);

        OrdersTelemetry.RecordProcessed("success");
        OrdersTelemetry.RecordProjectionLag(nameof(OrderStatusChanged), projectedAt - statusChanged.OccurredAt);
        return MessageProcessingResult.Processed;
    }

    private static Activity? StartActivity(
        ConsumeResult<string, byte[]> consumeResult,
        string correlationId,
        Guid eventId,
        Guid orderId)
    {
        var traceParent = GetHeader(consumeResult.Message.Headers, MessagingHeaders.TraceParent);
        var traceState = GetHeader(consumeResult.Message.Headers, MessagingHeaders.TraceState);
        var activity = OrdersTelemetry.StartActivity("orders.project", ActivityKind.Consumer, traceParent, traceState);
        activity?.SetTag("messaging.system", "kafka");
        activity?.SetTag("messaging.destination.name", consumeResult.Topic);
        activity?.SetTag("messaging.operation.type", "process");
        activity?.SetTag("messaging.message.id", eventId);
        activity?.SetTag("order.id", orderId);
        activity?.SetTag("correlation.id", correlationId);
        return activity;
    }

    private async Task<OrderCreated> DeserializeAvroAsync(
        ConsumeResult<string, byte[]> consumeResult,
        string description,
        CancellationToken cancellationToken)
    {
        try
        {
            var context = new SerializationContext(MessageComponentType.Value, consumeResult.Topic, consumeResult.Message.Headers);
            var record = await _avroDeserializer.DeserializeAsync(consumeResult.Message.Value, isNull: false, context)
                .WaitAsync(cancellationToken);
            return OrderCreatedAvroSchema.FromGenericRecord(record);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            throw new InvalidProjectionMessageException($"The Kafka message is not a valid {description}.", exception);
        }
    }

    private static T DeserializeJson<T>(byte[] payload, string description)
    {
        try
        {
            return JsonSerializer.Deserialize<T>(payload, SerializerOptions)
                ?? throw new JsonException($"The Kafka message did not contain a valid {description}.");
        }
        catch (JsonException exception)
        {
            throw new InvalidProjectionMessageException($"The Kafka message is not a valid {description}.", exception);
        }
    }

    private static string? GetHeader(Headers headers, string key)
    {
        var header = headers.LastOrDefault(item => string.Equals(item.Key, key, StringComparison.Ordinal));
        return header is null ? null : Encoding.UTF8.GetString(header.GetValueBytes());
    }
}
