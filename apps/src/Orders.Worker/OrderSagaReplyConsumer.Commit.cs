using BuildingBlocks;
using Npgsql;

namespace Orders.Worker;

// The other half of OrderSagaReplyConsumer.cs's own split - see that
// file's class comment. This one owns the CommitInventory reply, the
// ReleaseInventory reply, and the standalone settlement reconciliation
// (not a saga step - see HandleSettlementRepliedAsync's own comment).
public sealed partial class OrderSagaReplyConsumer
{
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
        // TryCompleteAndResolveAsync folds the Confirmed (and, when a line's
        // commit failed, the follow-up FulfillmentHold) transition into the
        // same transaction that deletes the saga row - payment was already
        // approved to reach this step, so a crash between "saga row gone"
        // and "order confirmed" used to leave a genuinely-paid order
        // permanently stuck at Created. See
        // SagaOrchestrationStore.TryCompleteAndResolveAsync's own comment.
        // The CancellationRequestedAt branch below must never confirm - the
        // resolution callback mirrors that same guard so the two can never
        // drift apart.
        var completed = await store.TryCompleteAndResolveAsync(
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
            (saga, connection, transaction, ct) => saga.CancellationRequestedAt is null
                ? ResolveCommitConfirmationWithinTransactionAsync(reply.OrderId, saga, connection, transaction, ct)
                : Task.CompletedTask,
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

        await cacheInvalidator.InvalidateAsync(reply.OrderId, cancellationToken);

        foreach (var line in completed.Lines.Where(line => line.Committed == true))
        {
            await RecordSaleBestEffortAsync(line.Sku, line.Quantity, cancellationToken);
        }
    }

    /// <summary>
    /// Confirms the order and, when at least one line's commit failed,
    /// follows up with FulfillmentHold - both against the connection and
    /// transaction TryCompleteAndResolveAsync is about to commit alongside
    /// the saga row's own deletion. Payment was already approved to reach
    /// this step, so the order is genuinely confirmed either way; a failed
    /// inventory commit isn't a reason to stay at Created, it's a reason
    /// for a human to look before the order ships - see
    /// SagaTimeoutSweeper.ResolveCommitInventoryWithinTransactionAsync for
    /// the identical two-step transition on the timeout path.
    /// </summary>
    private async Task ResolveCommitConfirmationWithinTransactionAsync(
        Guid orderId,
        SagaOrchestrationRecord saga,
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        CancellationToken cancellationToken)
    {
        await orderStatusStore.TryTransitionWithinTransactionAsync(
            connection, transaction, orderId, OrderStatuses.Confirmed, saga.CorrelationId, cancellationToken);

        var allCommitted = SagaLineCompletionPolicy.Commits(saga.Lines) == SagaLineCompletion.Succeeded;
        if (!allCommitted)
        {
            await orderStatusStore.TryTransitionWithinTransactionAsync(
                connection, transaction, orderId, OrderStatuses.FulfillmentHold, saga.CorrelationId, cancellationToken);
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

        // TryCompleteAndResolveAsync, not TryCompleteAsync - see
        // HandleReservationRepliedAsync's identical reasoning above for why
        // folding the cancellation into this same transaction matters.
        var completed = await store.TryCompleteAndResolveAsync(
            reply.OrderId,
            SagaStep.ReleaseInventory,
            _ => [],
            (saga, connection, transaction, ct) => orderStatusStore.TryTransitionWithinTransactionAsync(
                connection, transaction, reply.OrderId, OrderStatuses.Cancelled, saga.CorrelationId, ct),
            cancellationToken);
        if (completed is null)
        {
            SagaOrchestratorLog.UnknownReply(logger, reply.OrderId);
            return;
        }

        var allReleased = SagaLineCompletionPolicy.Releases(completed.Lines) == SagaLineCompletion.Succeeded;
        var outcome = allReleased ? "RejectedPaymentDeclined" : "RejectedPaymentDeclinedButReleaseFailed";
        var latencyMs = (reply.DecidedAt - completed.RequestedAt).TotalMilliseconds;
        SagaOrchestratorLog.SagaCompleted(logger, reply.OrderId, outcome, latencyMs, completed.CorrelationId);
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
}
