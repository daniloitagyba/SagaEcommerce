using BuildingBlocks;
using Npgsql;

namespace Orders.Worker;

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
            return;
        }

        var restockAt = timeProvider.GetUtcNow();
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
    /// Confirms an order after inventory commit.
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
            OrdersTelemetry.RecordOrphanedSagaReply("release", reply.Released);
            SagaOrchestratorLog.UnknownReply(logger, reply.OrderId);
            return;
        }

        if (SagaLineCompletionPolicy.Releases(lines) == SagaLineCompletion.Pending)
        {
            return;
        }

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
    /// Reconciles incomplete payment changes.
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
            OrdersTelemetry.RecordSettlementReconciliationUnresolved(moved.ToString());
            SagaOrchestratorLog.SettlementReconciliationDropped(logger, reply.OrderId, moved.ToString(), reply.CorrelationId);
        }
    }
}
