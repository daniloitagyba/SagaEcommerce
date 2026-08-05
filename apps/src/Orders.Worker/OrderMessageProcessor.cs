using System.Diagnostics;
using System.Text;
using Avro.Generic;
using BuildingBlocks;
using Confluent.Kafka;
using Confluent.SchemaRegistry;
using Confluent.SchemaRegistry.Serdes;
using Microsoft.Extensions.Options;

namespace Orders.Worker;

public enum MessageProcessingResult
{
    Processed,
    Duplicate
}

public sealed class InvalidOrderMessageException(string message, Exception? innerException = null)
    : Exception(message, innerException);

public sealed class OrderMessageProcessor(
    InboxStore inboxStore,
    ISchemaRegistryClient schemaRegistryClient,
    IOptions<KafkaOptions> options,
    ILogger<OrderMessageProcessor> logger)
{
    private readonly KafkaOptions _options = options.Value;
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
            "orders.process",
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

        await Task.Delay(TimeSpan.FromMilliseconds(25), cancellationToken);
        var inserted = await inboxStore.TryRecordAsync(
            _options.ConsumerGroup,
            orderCreated.EventId,
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
            WorkerLog.Duplicate(logger, orderCreated.EventId, _options.ConsumerGroup);
            return MessageProcessingResult.Duplicate;
        }

        OrdersTelemetry.RecordProcessed("success");
        WorkerLog.Processed(logger, orderCreated.OrderId, orderCreated.EventId, correlationId);
        return MessageProcessingResult.Processed;
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

        if (orderCreated.SchemaVersion != 1)
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

public sealed partial class WorkerLog
{
    [LoggerMessage(EventId = 2002, Level = LogLevel.Information, Message = "Processed order {OrderId} from event {EventId} with correlation {CorrelationId}")]
    public static partial void Processed(ILogger logger, Guid orderId, Guid eventId, string correlationId);

    [LoggerMessage(EventId = 2005, Level = LogLevel.Information, Message = "Skipped duplicate event {EventId} for consumer {ConsumerName}")]
    public static partial void Duplicate(ILogger logger, Guid eventId, string consumerName);
}
