using System.Text.Json;
using Avro.Generic;
using BuildingBlocks;
using Confluent.Kafka;
using Confluent.SchemaRegistry;
using Confluent.SchemaRegistry.Serdes;
using Microsoft.Extensions.Options;

namespace Orders.Worker;

/// <summary>
/// Appends order events to the event store.
/// </summary>
public sealed class OrderEventStoreProjector(
    IOptions<OrderEventStoreOptions> options,
    OrderEventStoreAppender appender,
    ISchemaRegistryClient schemaRegistryClient,
    ILogger<OrderEventStoreProjector> logger)
{
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);
    private readonly OrderEventStoreOptions _options = options.Value;
    private readonly AvroDeserializer<GenericRecord> _avroDeserializer = new(schemaRegistryClient);

    /// <summary>Public so integration tests can drive it directly, the same shape as the other saga classes' testable seams.</summary>
    public async Task AppendAsync(ConsumeResult<string, byte[]> consumeResult, CancellationToken cancellationToken)
    {
        if (consumeResult.Topic == _options.OrderCreatedTopic)
        {
            await AppendOrderCreatedAsync(consumeResult, cancellationToken);
        }
        else if (consumeResult.Topic == _options.PaymentResultTopic)
        {
            await AppendPaymentDecidedAsync(consumeResult.Message.Value, cancellationToken);
        }
        else if (consumeResult.Topic == _options.OrderStatusChangedTopic)
        {
            await AppendOrderStatusChangedAsync(consumeResult.Message.Value, cancellationToken);
        }
    }

    private async Task AppendOrderCreatedAsync(ConsumeResult<string, byte[]> consumeResult, CancellationToken cancellationToken)
    {
        var context = new SerializationContext(MessageComponentType.Value, consumeResult.Topic, consumeResult.Message.Headers);
        var record = await _avroDeserializer.DeserializeAsync(consumeResult.Message.Value, isNull: false, context)
            .WaitAsync(cancellationToken);
        var orderCreated = OrderCreatedAvroSchema.FromGenericRecord(record);

        var payload = JsonSerializer.Serialize(
            new { orderCreated.CustomerId, orderCreated.Amount, orderCreated.Currency },
            SerializerOptions);

        await appender.AppendAsync(orderCreated.OrderId, "OrderCreated", payload, orderCreated.OccurredAt, cancellationToken);
        OrderEventStoreLog.Appended(logger, orderCreated.OrderId, "OrderCreated");
    }

    private async Task AppendPaymentDecidedAsync(byte[] payload, CancellationToken cancellationToken)
    {
        var paymentDecided = JsonSerializer.Deserialize<PaymentDecided>(payload, SerializerOptions)
            ?? throw new JsonException("The Kafka message did not contain a valid PaymentDecided event.");

        var eventType = paymentDecided.Approved ? "OrderConfirmed" : "OrderCancelled";
        await appender.AppendAsync(paymentDecided.OrderId, eventType, "{}", paymentDecided.OccurredAt, cancellationToken);
        OrderEventStoreLog.Appended(logger, paymentDecided.OrderId, eventType);
    }

    /// <summary>
    /// Appends a warehouse move or a shopper's self-service cancellation to the timeline.
    /// </summary>
    private async Task AppendOrderStatusChangedAsync(byte[] payload, CancellationToken cancellationToken)
    {
        var statusChanged = JsonSerializer.Deserialize<OrderStatusChanged>(payload, SerializerOptions)
            ?? throw new JsonException("The Kafka message did not contain a valid OrderStatusChanged event.");

        var eventType = "Order" + statusChanged.Status;
        await appender.AppendAsync(statusChanged.OrderId, eventType, "{}", statusChanged.OccurredAt, cancellationToken);
        OrderEventStoreLog.Appended(logger, statusChanged.OrderId, eventType);
    }
}

public sealed partial class OrderEventStoreLog
{
    [LoggerMessage(EventId = 8003, Level = LogLevel.Information, Message = "Appended {EventType} event for order {OrderId}")]
    public static partial void Appended(ILogger logger, Guid orderId, string eventType);
}
