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
/// </summary>
public sealed class OrderSagaReplyConsumer(
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
            var completed = await store.TryCompleteAndQueueAsync(
                reply.OrderId,
                SagaStep.ReserveInventory,
                saga => saga.Lines
                    .Where(line => line.Reserved == true)
                    .Select(line => CreateReleaseCommand(reply.OrderId, saga.CorrelationId, line, now))
                    .ToList(),
                cancellationToken);
            if (completed is null)
            {
                SagaOrchestratorLog.UnknownReply(logger, reply.OrderId);
                return;
            }

            var latencyMs = (reply.DecidedAt - completed.RequestedAt).TotalMilliseconds;
            SagaOrchestratorLog.SagaCompleted(logger, reply.OrderId, "RejectedInsufficientStock", latencyMs, completed.CorrelationId);
            await orderStatusStore.TryCancelAsync(reply.OrderId, completed.CorrelationId, cancellationToken);
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

    private async Task HandleCommitRepliedAsync(string payload, CancellationToken cancellationToken)
    {
        var reply = Deserialize<InventoryReservationCommitReplied>(payload);
        if (reply is null)
        {
            return;
        }

        var lines = await store.RecordLineOutcomeAsync(reply.OrderId, reply.ReservationId, SagaLineOutcomeField.Committed, reply.Committed, cancellationToken);
        if (lines is null || lines.Count == 0)
        {
            OrdersTelemetry.RecordOrphanedSagaReply("commit", reply.Committed);
            SagaOrchestratorLog.UnknownReply(logger, reply.OrderId);
            return;
        }

        if (SagaLineCompletionPolicy.Commits(lines) == SagaLineCompletion.Pending)
        {
            // Still waiting on at least one more line's commit reply.
            return;
        }

        var restockAt = timeProvider.GetUtcNow();
        var completed = await store.TryCompleteAndQueueAsync(
            reply.OrderId,
            SagaStep.CommitInventory,
            saga => saga.CancellationRequestedAt is null
                ? []
                : saga.Lines
                    .Where(line => line.Committed == true)
                    .Select(line => SagaOutboxCommand.Create(
                        reply.OrderId,
                        _options.RestockRequestedTopic,
                        line.Sku,
                        new InventoryRestockRequested(
                            Guid.NewGuid(),
                            reply.OrderId,
                            line.Sku,
                            line.Quantity,
                            saga.CorrelationId,
                            restockAt),
                        saga.CorrelationId,
                        restockAt))
                    .ToList(),
            cancellationToken);
        if (completed is null)
        {
            SagaOrchestratorLog.UnknownReply(logger, reply.OrderId);
            return;
        }

        if (completed.CancellationRequestedAt is not null)
        {
            // Cancelled while this commit was in
            // flight. Payment was already approved to reach this step (and
            // its cancellation already requested, same as above) - what's
            // left is whatever stock the commit actually drew down for
            // real, which restocking gives back through the same command
            // returns and the replenishment loop
            // already use. Never confirm, never hold - the order is
            // already Cancelled, and no line here was ever sold.
            var cancelledLatencyMs = (reply.DecidedAt - completed.RequestedAt).TotalMilliseconds;
            SagaOrchestratorLog.SagaCompleted(logger, reply.OrderId, "CancelledWhileCommitting", cancelledLatencyMs, completed.CorrelationId);
            await cacheInvalidator.InvalidateAsync(reply.OrderId, cancellationToken);
            return;
        }

        var allCommitted = SagaLineCompletionPolicy.Commits(completed.Lines) == SagaLineCompletion.Succeeded;
        var outcome = allCommitted ? "Confirmed" : "ConfirmedButCommitFailed";
        var latencyMs = (reply.DecidedAt - completed.RequestedAt).TotalMilliseconds;
        SagaOrchestratorLog.SagaCompleted(logger, reply.OrderId, outcome, latencyMs, completed.CorrelationId);

        // Payment was already approved, so the order is genuinely confirmed
        // either way - a failed inventory commit isn't a reason to stay at Created.
        await orderStatusStore.TryConfirmAsync(reply.OrderId, completed.CorrelationId, cancellationToken);

        if (!allCommitted)
        {
            // Moves to FulfillmentHold - the customer is owed
            // something, but at least one line's stock was never actually
            // deducted, so a human has to resolve it before this can be picked.
            await orderStatusStore.TryTransitionAsync(
                reply.OrderId, OrderStatuses.FulfillmentHold, completed.CorrelationId, cancellationToken);
        }

        await cacheInvalidator.InvalidateAsync(reply.OrderId, cancellationToken);

        foreach (var line in completed.Lines.Where(line => line.Committed == true))
        {
            await RecordSaleBestEffortAsync(line.Sku, line.Quantity, cancellationToken);
        }
    }

    // Analytics side-effect, not a saga step: a failure here (Redis or
    // Catalog unreachable) must never fail or retry the saga completion it
    // reacts to - see BestsellersStore's class comment.
    private async Task RecordSaleBestEffortAsync(string sku, int quantity, CancellationToken cancellationToken)
    {
        try
        {
            var product = await catalogClient.FindBySkuAsync(sku, cancellationToken);
            await bestsellersStore.RecordSaleAsync(sku, product?.CategorySlug, quantity, cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            SagaOrchestratorLog.BestsellerTrackingFailed(logger, sku, exception);
        }
    }

    private async Task HandleReleaseRepliedAsync(string payload, CancellationToken cancellationToken)
    {
        var reply = Deserialize<InventoryReservationReleaseReplied>(payload);
        if (reply is null)
        {
            return;
        }

        var lines = await store.RecordLineOutcomeAsync(reply.OrderId, reply.ReservationId, SagaLineOutcomeField.Released, reply.Released, cancellationToken);
        if (lines is null || lines.Count == 0)
        {
            // Also reached for a release reply to a line that the
            // multi-line partial-failure compensation already published and
            // completed the order for (HandleReservationRepliedAsync) -
            // that path deletes the saga row immediately rather than
            // waiting at ReleaseInventory, so this is an expected, harmless no-op.
            OrdersTelemetry.RecordOrphanedSagaReply("release", reply.Released);
            SagaOrchestratorLog.UnknownReply(logger, reply.OrderId);
            return;
        }

        if (SagaLineCompletionPolicy.Releases(lines) == SagaLineCompletion.Pending)
        {
            // Still waiting on at least one more line's release reply.
            return;
        }

        var completed = await store.TryCompleteAsync(reply.OrderId, SagaStep.ReleaseInventory, cancellationToken);
        if (completed is null)
        {
            SagaOrchestratorLog.UnknownReply(logger, reply.OrderId);
            return;
        }

        var allReleased = SagaLineCompletionPolicy.Releases(completed.Lines) == SagaLineCompletion.Succeeded;
        var outcome = allReleased ? "RejectedPaymentDeclined" : "RejectedPaymentDeclinedButReleaseFailed";
        var latencyMs = (reply.DecidedAt - completed.RequestedAt).TotalMilliseconds;
        SagaOrchestratorLog.SagaCompleted(logger, reply.OrderId, outcome, latencyMs, completed.CorrelationId);
        await orderStatusStore.TryCancelAsync(reply.OrderId, completed.CorrelationId, cancellationToken);
        await cacheInvalidator.InvalidateAsync(reply.OrderId, cancellationToken);
    }

    /// <summary>
    /// Not a saga step - the saga row is already gone by the
    /// time an order ships, so there's nothing here to advance or
    /// complete. This is a standalone reconciliation for the outcome that
    /// must never pass silently: a capture, cancellation or refund that was
    /// supposed to apply but didn't, because the payment had already
    /// settled some other way (an expired hold, a decline, a prior void, a
    /// refund exceeding what's left to give back). Both PaymentAuthorizationSweeper's
    /// bulk expiry and every one of PaymentSettlementProcessor's
    /// settlement-mismatch replies land here through the same topic and
    /// carry <see cref="PaymentSettlementReplied.RequiresReconciliation"/> -
    /// previously this only recognized State == Expired by name, so a
    /// refund mismatch on a Voided or Declined payment (the return had
    /// already been accepted and restocked - only the money silently never
    /// moved) reached this exact method and was ignored.
    /// </summary>
    private async Task HandleSettlementRepliedAsync(string payload, CancellationToken cancellationToken)
    {
        var reply = Deserialize<PaymentSettlementReplied>(payload);
        if (reply is null || !reply.RequiresReconciliation)
        {
            return;
        }

        var moved = await orderStatusStore.TryTransitionAsync(
            reply.OrderId, OrderStatuses.FulfillmentHold, reply.CorrelationId, cancellationToken);

        if (moved == StatusTransitionResult.Transitioned)
        {
            await cacheInvalidator.InvalidateAsync(reply.OrderId, cancellationToken);
            SagaOrchestratorLog.SettlementReconciled(logger, reply.OrderId, reply.State, reply.CorrelationId);
        }
        else
        {
            // The one outcome the doc comment above says must never pass
            // silently - previously it did exactly that. NotApplicable can
            // be a benign race (someone else already moved this order);
            // IllegalTransition means the order can never legally reach
            // FulfillmentHold from its current state. Either way, money
            // that should have moved never did, and nothing downstream
            // was ever told.
            OrdersTelemetry.RecordSettlementReconciliationUnresolved(moved.ToString());
            SagaOrchestratorLog.SettlementReconciliationDropped(logger, reply.OrderId, moved.ToString(), reply.CorrelationId);
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
