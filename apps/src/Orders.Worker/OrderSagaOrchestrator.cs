using System.Diagnostics;
using Avro.Generic;
using BuildingBlocks;
using Confluent.Kafka;
using Confluent.SchemaRegistry;
using Confluent.SchemaRegistry.Serdes;
using Microsoft.Extensions.Options;

namespace Orders.Worker;

public sealed class InvalidSagaMessageException(string message, Exception? innerException = null)
    : Exception(message, innerException);

public sealed class OrderSagaOrchestrator(
    IOptions<SagaOrchestrationOptions> options,
    ISchemaRegistryClient schemaRegistryClient,
    SagaOrchestrationStore store,
    TimeProvider timeProvider,
    ILogger<OrderSagaOrchestrator> logger)
{
    private readonly SagaOrchestrationOptions _options = options.Value;
    private readonly AvroDeserializer<GenericRecord> _avroDeserializer = new(schemaRegistryClient);

    /// <summary>Public so integration tests can drive it directly, the same shape as the *MessageProcessor classes elsewhere in this codebase.</summary>
    public async Task RequestReservationAsync(ConsumeResult<string, byte[]> consumeResult, CancellationToken cancellationToken)
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
            throw new InvalidSagaMessageException("The Kafka message is not a valid OrderCreated event.", exception);
        }

        var requestedAt = timeProvider.GetUtcNow();

        if (orderCreated.LinesOrEmpty.Count == 0)
        {
            SagaOrchestratorLog.SkippedOrderWithoutLines(logger, orderCreated.OrderId, orderCreated.CorrelationId);
            return;
        }

        var lines = orderCreated.LinesOrEmpty
            .Select(line => new SagaReservationLine(Guid.NewGuid(), line.Sku, line.Quantity))
            .ToList();

        var commands = lines
            .Select(line => SagaOutboxCommand.Create(
                orderCreated.OrderId,
                _options.ReservationRequestedTopic,
                line.Sku,
                new InventoryReservationRequested(
                    line.ReservationId,
                    orderCreated.OrderId,
                    line.Sku,
                    line.Quantity,
                    orderCreated.CorrelationId,
                    requestedAt),
                orderCreated.CorrelationId,
                requestedAt))
            .ToList();

        await store.TrackReserveRequestedAndQueueAsync(
            orderCreated.OrderId,
            orderCreated.CorrelationId,
            orderCreated.CustomerId,
            orderCreated.PaymentMethod,
            orderCreated.ShippingPostalPrefix,
            lines,
            orderCreated.Amount,
            orderCreated.Currency,
            requestedAt,
            commands,
            cancellationToken);

        using var activity = OrdersTelemetry.StartActivity("saga.orchestrator.reserve", ActivityKind.Producer, null, null);
        activity?.SetTag("order.id", orderCreated.OrderId);
        activity?.SetTag("inventory.line_count", lines.Count);
        activity?.SetTag("correlation.id", orderCreated.CorrelationId);

        foreach (var line in lines)
        {
            SagaOrchestratorLog.ReservationRequested(logger, orderCreated.OrderId, line.Sku, orderCreated.CorrelationId);
        }
    }
}

public sealed partial class SagaOrchestratorLog
{
    [LoggerMessage(EventId = 6003, Level = LogLevel.Warning, Message = "Saga orchestrator received an invalid OrderCreated message")]
    public static partial void InvalidMessage(ILogger logger, Exception exception);

    [LoggerMessage(EventId = 6004, Level = LogLevel.Information, Message = "OrchestratedSagaReservationQueued order {OrderId} sku {Sku} correlation {CorrelationId}")]
    public static partial void ReservationRequested(ILogger logger, Guid orderId, string sku, string correlationId);

    [LoggerMessage(EventId = 6006, Level = LogLevel.Information, Message = "OrchestratedSagaCompleted order {OrderId} outcome={Outcome} finalStepLatencyMs={LatencyMs} correlation {CorrelationId}")]
    public static partial void SagaCompleted(ILogger logger, Guid orderId, string outcome, double latencyMs, string correlationId);

    [LoggerMessage(EventId = 6007, Level = LogLevel.Warning, Message = "OrchestratedSagaTimedOut order {OrderId} at step {Step} after {TimeoutSeconds}s correlation {CorrelationId}")]
    public static partial void SagaTimedOut(ILogger logger, Guid orderId, string step, int timeoutSeconds, string correlationId);

    [LoggerMessage(EventId = 6008, Level = LogLevel.Warning, Message = "Saga orchestrator received a reply for an unknown, already-settled, or out-of-order order {OrderId}")]
    public static partial void UnknownReply(ILogger logger, Guid orderId);

    [LoggerMessage(EventId = 6009, Level = LogLevel.Information, Message = "OrchestratedSagaAdvanced order {OrderId} step {Step} correlation {CorrelationId}")]
    public static partial void Advanced(ILogger logger, Guid orderId, string step, string correlationId);

    [LoggerMessage(EventId = 6011, Level = LogLevel.Warning, Message = "Bestseller tracking failed for sku {Sku} - the sale is not lost, only its ranking contribution is")]
    public static partial void BestsellerTrackingFailed(ILogger logger, string sku, Exception exception);

    [LoggerMessage(EventId = 6012, Level = LogLevel.Information, Message = "Saga orchestrator skipped order {OrderId} because it carries no line items to reserve correlation {CorrelationId}")]
    public static partial void SkippedOrderWithoutLines(ILogger logger, Guid orderId, string correlationId);

    [LoggerMessage(EventId = 6013, Level = LogLevel.Information, Message = "Order {OrderId} backordered for sku {Sku} - waiting for a restock, saga parked at ReserveInventory, correlation {CorrelationId}")]
    public static partial void Backordered(ILogger logger, Guid orderId, string sku, string correlationId);

    [LoggerMessage(EventId = 6014, Level = LogLevel.Warning, Message = "Order {OrderId} moved to FulfillmentHold - a settlement reply came back {State} instead of Captured, correlation {CorrelationId}")]
    public static partial void SettlementReconciled(ILogger logger, Guid orderId, string state, string correlationId);

    [LoggerMessage(EventId = 6015, Level = LogLevel.Information, Message = "Saga timeout for order {OrderId} queued reservation release for sku {Sku}, correlation {CorrelationId}")]
    public static partial void TimeoutReleaseRequested(ILogger logger, Guid orderId, string sku, string correlationId);

    [LoggerMessage(EventId = 6016, Level = LogLevel.Warning, Message = "Settlement reconciliation for order {OrderId} was dropped: FulfillmentHold transition returned {TransitionResult} instead of Transitioned - a settlement expired but nothing downstream was told, correlation {CorrelationId}")]
    public static partial void SettlementReconciliationDropped(ILogger logger, Guid orderId, string transitionResult, string correlationId);
}
