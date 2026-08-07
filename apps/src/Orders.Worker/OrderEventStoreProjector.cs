using System.Text.Json;
using Avro.Generic;
using BuildingBlocks;
using Confluent.Kafka;
using Confluent.SchemaRegistry;
using Confluent.SchemaRegistry.Serdes;
using Microsoft.Extensions.Options;

namespace Orders.Worker;

/// <summary>
/// Milestone 23: appends to the append-only order_events store - a second,
/// additive consumer group on the same topics the choreographed CQRS
/// projector already reads. No inbox dedup here, a deliberate scope
/// boundary: a redelivered message can append a duplicate event, which the
/// fold in GetOrderHistoryHandler doesn't guard against either.
/// </summary>
public sealed class OrderEventStoreProjector(
    IOptions<OrderEventStoreOptions> options,
    OrderEventStoreAppender appender,
    ISchemaRegistryClient schemaRegistryClient,
    ILogger<OrderEventStoreProjector> logger) : BackgroundService
{
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);
    private readonly OrderEventStoreOptions _options = options.Value;
    private readonly AvroDeserializer<GenericRecord> _avroDeserializer = new(schemaRegistryClient);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var config = new ConsumerConfig
        {
            BootstrapServers = _options.BootstrapServers,
            GroupId = _options.ConsumerGroup,
            ClientId = _options.ClientId,
            AutoOffsetReset = AutoOffsetReset.Earliest,
            EnableAutoCommit = true,
            AutoCommitIntervalMs = 1_000,
            AllowAutoCreateTopics = false
        };

        using var consumer = new ConsumerBuilder<string, byte[]>(config).Build();
        consumer.Subscribe([_options.OrderCreatedTopic, _options.PaymentResultTopic]);
        OrderEventStoreLog.Started(logger, _options.OrderCreatedTopic, _options.PaymentResultTopic, _options.ConsumerGroup);

        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                ConsumeResult<string, byte[]> consumeResult;
                try
                {
                    consumeResult = consumer.Consume(stoppingToken);
                }
                catch (ConsumeException exception)
                {
                    OrderEventStoreLog.ConsumeFailed(logger, exception.Error.Reason, exception);
                    await Task.Delay(1_000, stoppingToken);
                    continue;
                }

                await AppendAsync(consumeResult, stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            OrderEventStoreLog.Stopping(logger);
        }
        finally
        {
            consumer.Close();
        }
    }

    private async Task AppendAsync(ConsumeResult<string, byte[]> consumeResult, CancellationToken cancellationToken)
    {
        try
        {
            if (consumeResult.Topic == _options.OrderCreatedTopic)
            {
                await AppendOrderCreatedAsync(consumeResult, cancellationToken);
            }
            else if (consumeResult.Topic == _options.PaymentResultTopic)
            {
                await AppendPaymentDecidedAsync(consumeResult.Message.Value, cancellationToken);
            }
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            OrderEventStoreLog.AppendFailed(logger, consumeResult.Topic, exception);
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
}

public sealed partial class OrderEventStoreLog
{
    [LoggerMessage(EventId = 8000, Level = LogLevel.Information, Message = "Order event store projector subscribed to topics {TopicA} and {TopicB} with consumer group {GroupId}")]
    public static partial void Started(ILogger logger, string topicA, string topicB, string groupId);

    [LoggerMessage(EventId = 8001, Level = LogLevel.Information, Message = "Order event store projector is stopping gracefully")]
    public static partial void Stopping(ILogger logger);

    [LoggerMessage(EventId = 8002, Level = LogLevel.Error, Message = "Order event store projector Kafka consume failed: {Reason}")]
    public static partial void ConsumeFailed(ILogger logger, string reason, Exception exception);

    [LoggerMessage(EventId = 8003, Level = LogLevel.Information, Message = "Appended {EventType} event for order {OrderId}")]
    public static partial void Appended(ILogger logger, Guid orderId, string eventType);

    [LoggerMessage(EventId = 8004, Level = LogLevel.Error, Message = "Order event store projector failed to append an event from topic {Topic}")]
    public static partial void AppendFailed(ILogger logger, string topic, Exception exception);
}
