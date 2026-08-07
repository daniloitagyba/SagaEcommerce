using System.Diagnostics;
using System.Text.Json;
using Avro.Generic;
using BuildingBlocks;
using Confluent.Kafka;
using Confluent.SchemaRegistry;
using Confluent.SchemaRegistry.Serdes;
using Microsoft.Extensions.Options;

namespace Orders.Worker;

/// <summary>
/// Milestone 22: an explicit orchestrator for the same order-created event
/// the choreographed saga already reacts to (a second, independent consumer
/// group on orders.created.v1 - additive, the existing choreography is
/// completely untouched). Where Payments.Service's choreographed consumer
/// autonomously decides on seeing OrderCreated, this orchestrator explicitly
/// requests a decision and owns the timeout if one never arrives -
/// something choreography has no equivalent for.
///
/// Milestone 43: step 1 of what's now a 4-step saga (Reserve Inventory ->
/// Decide Payment -> Commit or Release Inventory -> saga closed). This
/// class only kicks off step 1; OrderSagaReplyConsumer drives every
/// subsequent transition as replies arrive, since only one reply is ever
/// outstanding per order at a time.
/// </summary>
public sealed class OrderSagaOrchestrator(
    IOptions<SagaOrchestrationOptions> options,
    IProducer<string, string> producer,
    ISchemaRegistryClient schemaRegistryClient,
    SagaOrchestrationStore store,
    ILogger<OrderSagaOrchestrator> logger) : BackgroundService
{
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);
    private readonly SagaOrchestrationOptions _options = options.Value;
    private readonly AvroDeserializer<GenericRecord> _avroDeserializer = new(schemaRegistryClient);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var config = new ConsumerConfig
        {
            BootstrapServers = _options.BootstrapServers,
            GroupId = _options.RequestConsumerGroup,
            ClientId = _options.ClientId,
            AutoOffsetReset = AutoOffsetReset.Earliest,
            EnableAutoCommit = true,
            AutoCommitIntervalMs = 1_000,
            AllowAutoCreateTopics = false
        };

        using var consumer = new ConsumerBuilder<string, byte[]>(config).Build();
        consumer.Subscribe(_options.OrderCreatedTopic);
        SagaOrchestratorLog.Started(logger, _options.OrderCreatedTopic, _options.RequestConsumerGroup);

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
                    SagaOrchestratorLog.ConsumeFailed(logger, exception.Error.Reason, exception);
                    await Task.Delay(1_000, stoppingToken);
                    continue;
                }

                await RequestReservationAsync(consumeResult, stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            SagaOrchestratorLog.Stopping(logger);
        }
        finally
        {
            consumer.Close();
        }
    }

    private async Task RequestReservationAsync(ConsumeResult<string, byte[]> consumeResult, CancellationToken cancellationToken)
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
            SagaOrchestratorLog.InvalidMessage(logger, exception);
            return;
        }

        var requestedAt = DateTimeOffset.UtcNow;
        var reservationId = Guid.NewGuid();

        // Milestone 66: reserve what the customer actually ordered. Until
        // now this hashed the order id to pick one of nine seeded SKUs
        // (SagaSkuMapper, deleted), so every "reservation" in every prior
        // saga demonstration held stock for a product nobody bought.
        //
        // The saga still tracks a single line: SagaOrchestrationState is
        // one row per order with one sku/quantity, and widening it into a
        // per-line state machine (partial reservations, per-line
        // compensation) is a genuinely larger change than this milestone
        // is making. Taking the largest line by value is a stated
        // simplification rather than a hidden one - but it is now a real
        // line from a real order.
        var primaryLine = orderCreated.LinesOrEmpty
            .OrderByDescending(line => line.LineTotal)
            .ThenBy(line => line.Sku, StringComparer.Ordinal)
            .FirstOrDefault();

        if (primaryLine is null)
        {
            // An amount-only order (schema v1, or the legacy request shape)
            // has nothing to reserve. Skipping is right: inventing a SKU is
            // exactly the behaviour this milestone removed.
            SagaOrchestratorLog.SkippedOrderWithoutLines(logger, orderCreated.OrderId, orderCreated.CorrelationId);
            return;
        }

        var sku = primaryLine.Sku;
        var quantity = primaryLine.Quantity;

        await store.TrackReserveRequestedAsync(
            orderCreated.OrderId,
            orderCreated.CorrelationId,
            orderCreated.CustomerId,
            orderCreated.PaymentMethod,
            orderCreated.ShippingPostalPrefix,
            reservationId,
            sku,
            quantity,
            orderCreated.Amount,
            orderCreated.Currency,
            requestedAt,
            cancellationToken);

        var request = new InventoryReservationRequested(
            reservationId,
            orderCreated.OrderId,
            sku,
            quantity,
            orderCreated.CorrelationId,
            requestedAt);

        using var activity = OrdersTelemetry.StartActivity("saga.orchestrator.reserve", ActivityKind.Producer, null, null);
        activity?.SetTag("order.id", orderCreated.OrderId);
        activity?.SetTag("inventory.sku", sku);
        activity?.SetTag("correlation.id", orderCreated.CorrelationId);

        // Keyed by Sku, not OrderId - see BuildingBlocks/InventoryContracts.cs.
        var message = new Message<string, string>
        {
            Key = sku,
            Value = JsonSerializer.Serialize(request, SerializerOptions)
        };

        try
        {
            await producer.ProduceAsync(_options.ReservationRequestedTopic, message, cancellationToken);
            SagaOrchestratorLog.ReservationRequested(logger, orderCreated.OrderId, sku, orderCreated.CorrelationId);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            SagaOrchestratorLog.RequestPublishFailed(logger, orderCreated.OrderId, exception);
        }
    }
}

public sealed partial class SagaOrchestratorLog
{
    [LoggerMessage(EventId = 6000, Level = LogLevel.Information, Message = "Saga orchestrator subscribed to topic {Topic} with consumer group {GroupId}")]
    public static partial void Started(ILogger logger, string topic, string groupId);

    [LoggerMessage(EventId = 6001, Level = LogLevel.Information, Message = "Saga orchestrator is stopping gracefully")]
    public static partial void Stopping(ILogger logger);

    [LoggerMessage(EventId = 6002, Level = LogLevel.Error, Message = "Saga orchestrator Kafka consume failed: {Reason}")]
    public static partial void ConsumeFailed(ILogger logger, string reason, Exception exception);

    [LoggerMessage(EventId = 6003, Level = LogLevel.Warning, Message = "Saga orchestrator received an invalid OrderCreated message")]
    public static partial void InvalidMessage(ILogger logger, Exception exception);

    [LoggerMessage(EventId = 6004, Level = LogLevel.Information, Message = "OrchestratedSagaReservationRequested order {OrderId} sku {Sku} correlation {CorrelationId}")]
    public static partial void ReservationRequested(ILogger logger, Guid orderId, string sku, string correlationId);

    [LoggerMessage(EventId = 6005, Level = LogLevel.Error, Message = "Saga orchestrator failed to publish reservation request for order {OrderId}")]
    public static partial void RequestPublishFailed(ILogger logger, Guid orderId, Exception exception);

    [LoggerMessage(EventId = 6006, Level = LogLevel.Information, Message = "OrchestratedSagaCompleted order {OrderId} outcome={Outcome} finalStepLatencyMs={LatencyMs} correlation {CorrelationId}")]
    public static partial void SagaCompleted(ILogger logger, Guid orderId, string outcome, double latencyMs, string correlationId);

    [LoggerMessage(EventId = 6007, Level = LogLevel.Warning, Message = "OrchestratedSagaTimedOut order {OrderId} at step {Step} after {TimeoutSeconds}s correlation {CorrelationId}")]
    public static partial void SagaTimedOut(ILogger logger, Guid orderId, string step, int timeoutSeconds, string correlationId);

    [LoggerMessage(EventId = 6008, Level = LogLevel.Warning, Message = "Saga orchestrator received a reply for an unknown, already-settled, or out-of-order order {OrderId}")]
    public static partial void UnknownReply(ILogger logger, Guid orderId);

    [LoggerMessage(EventId = 6009, Level = LogLevel.Information, Message = "OrchestratedSagaAdvanced order {OrderId} step {Step} correlation {CorrelationId}")]
    public static partial void Advanced(ILogger logger, Guid orderId, string step, string correlationId);

    [LoggerMessage(EventId = 6010, Level = LogLevel.Error, Message = "Saga orchestrator failed to publish the next step's request for order {OrderId}")]
    public static partial void NextStepPublishFailed(ILogger logger, Guid orderId, Exception exception);

    [LoggerMessage(EventId = 6011, Level = LogLevel.Warning, Message = "Bestseller tracking failed for sku {Sku} - the sale is not lost, only its ranking contribution is")]
    public static partial void BestsellerTrackingFailed(ILogger logger, string sku, Exception exception);

    [LoggerMessage(EventId = 6012, Level = LogLevel.Information, Message = "Saga orchestrator skipped order {OrderId} because it carries no line items to reserve correlation {CorrelationId}")]
    public static partial void SkippedOrderWithoutLines(ILogger logger, Guid orderId, string correlationId);

    [LoggerMessage(EventId = 6013, Level = LogLevel.Information, Message = "Order {OrderId} backordered for sku {Sku} - waiting for a restock, saga parked at ReserveInventory, correlation {CorrelationId}")]
    public static partial void Backordered(ILogger logger, Guid orderId, string sku, string correlationId);
}
