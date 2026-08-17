using System.Text.Json;
using BuildingBlocks;
using Confluent.Kafka;
using Microsoft.Extensions.Options;

namespace Orders.Worker;

/// <summary>
/// Consumes replies for order sagas.
/// </summary>
public sealed partial class OrderSagaReplyConsumer(
    IOptions<SagaOrchestrationOptions> options,
    SagaOrchestrationStore store,
    OrderStatusStore orderStatusStore,
    IOrderCacheInvalidator cacheInvalidator,
    IBestsellersStore bestsellersStore,
    ICatalogClient catalogClient,
    TimeProvider timeProvider,
    ILogger<OrderSagaReplyConsumer> logger)
{
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);
    private readonly SagaOrchestrationOptions _options = options.Value;

    /// <summary>Public so integration tests can drive it directly, the same shape as OrderSagaOrchestrator.RequestReservationAsync.</summary>
    public Task DispatchAsync(ConsumeResult<string, string> consumeResult, CancellationToken cancellationToken)
    {
        return consumeResult.Topic switch
        {
            _ when consumeResult.Topic == _options.ReservationRepliedTopic => HandleReservationRepliedAsync(consumeResult.Message.Value, cancellationToken),
            _ when consumeResult.Topic == _options.DecisionRepliedTopic => HandlePaymentDecisionRepliedAsync(consumeResult.Message.Value, cancellationToken),
            _ when consumeResult.Topic == _options.CommitRepliedTopic => HandleCommitRepliedAsync(consumeResult.Message.Value, cancellationToken),
            _ when consumeResult.Topic == _options.ReleaseRepliedTopic => HandleReleaseRepliedAsync(consumeResult.Message.Value, cancellationToken),
            _ when consumeResult.Topic == _options.SettlementRepliedTopic => HandleSettlementRepliedAsync(consumeResult.Message.Value, cancellationToken),
            _ => Task.CompletedTask
        };
    }

    private async Task HandleReservationRepliedAsync(string payload, CancellationToken cancellationToken)
    {
        var reply = Deserialize<InventoryReservationReplied>(payload);
        if (reply is null)
        {
            return;
        }

        if (!reply.Reserved && reply.Backordered)
        {
            await orderStatusStore.TryTransitionAsync(
                reply.OrderId, OrderStatuses.Backordered, reply.CorrelationId, cancellationToken);
            await store.MarkParkedAsync(reply.OrderId, timeProvider.GetUtcNow(), cancellationToken);
            await cacheInvalidator.InvalidateAsync(reply.OrderId, cancellationToken);
            SagaOrchestratorLog.Backordered(logger, reply.OrderId, reply.Sku, reply.CorrelationId);
            return;
        }

        var lines = await store.RecordLineOutcomeAsync(reply.OrderId, reply.ReservationId, SagaLineOutcomeField.Reserved, reply.Reserved, cancellationToken);
        if (lines is null || lines.Count == 0)
        {
            OrdersTelemetry.RecordOrphanedSagaReply("reservation", reply.Reserved);
            SagaOrchestratorLog.UnknownReply(logger, reply.OrderId);
            return;
        }

        var reservationCompletion = SagaLineCompletionPolicy.Reservations(lines);
        if (reservationCompletion == SagaLineCompletion.Failed)
        {
            var now = timeProvider.GetUtcNow();
            var completed = await store.TryCompleteAndResolveAsync(
                reply.OrderId,
                SagaStep.ReserveInventory,
                saga => saga.Lines
                    .Where(line => line.Reserved == true)
                    .Select(line => CreateReleaseCommand(reply.OrderId, saga.CorrelationId, line, now))
                    .ToList(),
                (saga, connection, transaction, ct) => orderStatusStore.TryTransitionWithinTransactionAsync(
                    connection, transaction, reply.OrderId, OrderStatuses.Cancelled, saga.CorrelationId, ct),
                cancellationToken);
            if (completed is null)
            {
                SagaOrchestratorLog.UnknownReply(logger, reply.OrderId);
                return;
            }

            var latencyMs = (reply.DecidedAt - completed.RequestedAt).TotalMilliseconds;
            SagaOrchestratorLog.SagaCompleted(logger, reply.OrderId, "RejectedInsufficientStock", latencyMs, completed.CorrelationId);
            await cacheInvalidator.InvalidateAsync(reply.OrderId, cancellationToken);
            return;
        }

        if (reservationCompletion == SagaLineCompletion.Pending)
        {
            return;
        }

        var advanceAt = timeProvider.GetUtcNow();
        var advanced = await store.TryAdvanceAndQueueAsync(
            reply.OrderId,
            SagaStep.ReserveInventory,
            SagaStep.DecidePayment,
            advanceAt,
            saga => saga.CancellationRequestedAt is not null
                ? []
                :
                [
                    SagaOutboxCommand.Create(
                        reply.OrderId,
                        _options.DecisionRequestedTopic,
                        reply.OrderId.ToString("N"),
                        new PaymentDecisionRequested(
                            reply.OrderId,
                            saga.Amount,
                            saga.Currency,
                            saga.CorrelationId,
                            advanceAt,
                            saga.CustomerId,
                            saga.PaymentMethod,
                            saga.ShippingPostalPrefix),
                        saga.CorrelationId,
                        advanceAt)
                ],
            cancellationToken);
        if (advanced is null)
        {
            SagaOrchestratorLog.UnknownReply(logger, reply.OrderId);
            return;
        }

        if (advanced.CancellationRequestedAt is not null)
        {
            await CancelDuringSagaAsync(reply.OrderId, SagaStep.DecidePayment, "CancelledWhileReserving", cancellationToken);
            return;
        }

        SagaOrchestratorLog.Advanced(logger, reply.OrderId, SagaStep.DecidePayment, advanced.CorrelationId);

    }

    /// <summary>
    /// Completes a saga and releases reserved inventory.
    /// </summary>
    private async Task CancelDuringSagaAsync(Guid orderId, string expectedStep, string outcome, CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        var completed = await store.TryCompleteAndQueueAsync(
            orderId,
            expectedStep,
            saga => saga.Lines
                .Where(line => line.Reserved == true)
                .Select(line => CreateReleaseCommand(orderId, saga.CorrelationId, line, now))
                .ToList(),
            cancellationToken);
        if (completed is null)
        {
            SagaOrchestratorLog.UnknownReply(logger, orderId);
            return;
        }

        var latencyMs = (now - completed.RequestedAt).TotalMilliseconds;
        SagaOrchestratorLog.SagaCompleted(logger, orderId, outcome, latencyMs, completed.CorrelationId);
        await cacheInvalidator.InvalidateAsync(orderId, cancellationToken);
    }

    private async Task HandlePaymentDecisionRepliedAsync(string payload, CancellationToken cancellationToken)
    {
        var reply = Deserialize<PaymentDecisionReplied>(payload);
        if (reply is null)
        {
            return;
        }

        var now = timeProvider.GetUtcNow();

        if (reply.Approved)
        {
            var advanced = await store.TryAdvanceAndQueueAsync(
                reply.OrderId,
                SagaStep.DecidePayment,
                SagaStep.CommitInventory,
                now,
                saga => saga.Lines
                    .Select(line => SagaOutboxCommand.Create(
                        reply.OrderId,
                        _options.CommitRequestedTopic,
                        line.Sku,
                        new InventoryReservationCommitRequested(
                            line.ReservationId,
                            reply.OrderId,
                            line.Sku,
                            line.Quantity,
                            saga.CorrelationId,
                            now),
                        saga.CorrelationId,
                        now))
                    .ToList(),
                cancellationToken);
            if (advanced is null)
            {
                SagaOrchestratorLog.UnknownReply(logger, reply.OrderId);
                return;
            }

            SagaOrchestratorLog.Advanced(logger, reply.OrderId, SagaStep.CommitInventory, advanced.CorrelationId);
        }
        else
        {
            var advanced = await store.TryAdvanceAndQueueAsync(
                reply.OrderId,
                SagaStep.DecidePayment,
                SagaStep.ReleaseInventory,
                now,
                saga => saga.Lines
                    .Select(line => CreateReleaseCommand(reply.OrderId, saga.CorrelationId, line, now))
                    .ToList(),
                cancellationToken);
            if (advanced is null)
            {
                SagaOrchestratorLog.UnknownReply(logger, reply.OrderId);
                return;
            }

            SagaOrchestratorLog.Advanced(logger, reply.OrderId, SagaStep.ReleaseInventory, advanced.CorrelationId);
        }
    }

    private SagaOutboxCommand CreateReleaseCommand(
        Guid orderId,
        string correlationId,
        SagaLineRecord line,
        DateTimeOffset occurredAt) =>
        SagaOutboxCommand.Create(
            orderId,
            _options.ReleaseRequestedTopic,
            line.Sku,
            new InventoryReservationReleaseRequested(
                line.ReservationId,
                orderId,
                line.Sku,
                line.Quantity,
                correlationId,
                occurredAt),
            correlationId,
            occurredAt);

    private static T? Deserialize<T>(string payload)
    {
        try
        {
            return JsonSerializer.Deserialize<T>(payload, SerializerOptions)
                ?? throw new JsonException($"The {typeof(T).Name} payload deserialized to null.");
        }
        catch (JsonException exception)
        {
            throw new InvalidSagaReplyMessageException(
                $"The Kafka message is not a valid {typeof(T).Name} reply.",
                exception);
        }
    }
}

public sealed class InvalidSagaReplyMessageException(string message, Exception? innerException = null)
    : Exception(message, innerException);
