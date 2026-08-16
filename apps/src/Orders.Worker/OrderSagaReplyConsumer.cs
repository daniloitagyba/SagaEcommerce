using System.Text.Json;
using BuildingBlocks;
using Confluent.Kafka;
using Microsoft.Extensions.Options;

namespace Orders.Worker;

/// <summary>
/// The choreographed comparison's compensation half, extended into the
/// driver of a 4-step saga. One consumer subscribing to all four reply
/// topics and dispatching by topic name, since only one reply is ever
/// outstanding per order at a time.
///
/// State machine:
///   ReserveInventory  --(reserved)-->     DecidePayment   --(approved)--> CommitInventory --> done (Confirmed)
///   ReserveInventory  --(insufficient)--> done (RejectedInsufficientStock)
///   DecidePayment     --(declined)-->     ReleaseInventory (the compensation) --> done (RejectedPaymentDeclined)
///
/// Split across two files to stay under the 500-line module-size budget,
/// the same physical-split-not-different-concern pattern
/// SagaOrchestrationStore uses for its own split: this file owns dispatch,
/// the ReserveInventory reply and the DecidePayment reply;
/// OrderSagaReplyConsumer.Commit.cs owns the CommitInventory/
/// ReleaseInventory replies and the standalone settlement reconciliation.
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
            // Wait, don't give up - this line stays
            // unanswered (neither Reserved nor rejected) until the
            // eventual backorder-release reply flips it. The
            // *order* moves to Backordered even if a sibling line already
            // reserved fine - that reservation is held, not released,
            // until every line has an answer.
            await orderStatusStore.TryTransitionAsync(
                reply.OrderId, OrderStatuses.Backordered, reply.CorrelationId, cancellationToken);
            // Parks the row against SagaTimeoutSweeper's own timeout - see SagaOrchestrationState.ParkedAt. Idempotent (keeps the earliest ParkedAt).
            await store.MarkParkedAsync(reply.OrderId, timeProvider.GetUtcNow(), cancellationToken);
            await cacheInvalidator.InvalidateAsync(reply.OrderId, cancellationToken);
            SagaOrchestratorLog.Backordered(logger, reply.OrderId, reply.Sku, reply.CorrelationId);
            return;
        }

        var lines = await store.RecordLineOutcomeAsync(reply.OrderId, reply.ReservationId, SagaLineOutcomeField.Reserved, reply.Reserved, cancellationToken);
        if (lines is null || lines.Count == 0)
        {
            // Either an unknown reservation, or the order's saga row is
            // already gone (completed by a sibling line's rejection, or by
            // a timeout) - a redelivered/late reply for it is a no-op.
            // Reserved:true landing here specifically means Inventory just
            // created an allocation nothing will ever release - see
            // OrdersTelemetry.RecordOrphanedSagaReply's own comment.
            OrdersTelemetry.RecordOrphanedSagaReply("reservation", reply.Reserved);
            SagaOrchestratorLog.UnknownReply(logger, reply.OrderId);
            return;
        }

        var reservationCompletion = SagaLineCompletionPolicy.Reservations(lines);
        if (reservationCompletion == SagaLineCompletion.Failed)
        {
            // The multi-line compensation case: at least one line was
            // rejected outright. Release every sibling line that DID
            // reserve successfully before cancelling - the whole order
            // fails together, so a partial reservation left behind would
            // be inventory nothing will ever release.
            var now = timeProvider.GetUtcNow();
            // TryCompleteAndResolveAsync, not TryCompleteAndQueueAsync -
            // folds the order's cancellation into the very transaction
            // that deletes the saga row, so a crash between "stock release
            // queued" and "order cancelled" can no longer strand the order
            // non-terminal with nothing left to time out again. See
            // SagaOrchestrationStore.TryCompleteAndResolveAsync's own comment.
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
            // Still waiting on at least one more line's reply.
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
            // An operator or the shopper themselves
            // cancelled this order (from Created or Backordered) while this
            // very reservation was in flight - every line just finished
            // reserving successfully, at the exact moment nothing needs
            // them any more. Give them back instead of asking Payments for
            // a decision nobody wants any more; a payment cancellation was
            // already requested unconditionally by whatever cancelled the
            // order (EfOrderStatusRepository.TryTransitionAsync), so
            // there's nothing to do here for money, only for stock.
            await CancelDuringSagaAsync(reply.OrderId, SagaStep.DecidePayment, "CancelledWhileReserving", cancellationToken);
            return;
        }

        SagaOrchestratorLog.Advanced(logger, reply.OrderId, SagaStep.DecidePayment, advanced.CorrelationId);

    }

    /// <summary>
    /// The shared half of both cancellation-mid-saga
    /// branches below that release rather than commit - completes the saga
    /// row from wherever it currently stands and releases every line that
    /// is known, from this same row's own lines snapshot, to have actually
    /// reserved. Mirrors HandleReservationRepliedAsync's existing
    /// partial-rejection compensation rather than inventing
    /// a new shape for "release everything and stop".
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
            // The compensating transaction: undo the step 1 reservations, since payment was the problem, not them.
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
