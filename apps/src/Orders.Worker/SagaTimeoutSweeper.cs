using BuildingBlocks;
using Microsoft.Extensions.Options;
using Npgsql;

namespace Orders.Worker;

/// <summary>
/// Resolves orchestrated sagas whose downstream service did not reply.
/// </summary>
public sealed class SagaTimeoutSweeper(
    IOptions<SagaOrchestrationOptions> options,
    SagaOrchestrationStore store,
    OrderStatusStore orderStatusStore,
    ILeaderElection leaderElection,
    TimeProvider timeProvider,
    ILogger<SagaTimeoutSweeper> logger) : BackgroundService
{
    private const int SweepBatchSize = 100;
    private readonly SagaOrchestrationOptions _options = options.Value;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var timeout = TimeSpan.FromSeconds(_options.TimeoutSeconds);

        while (!stoppingToken.IsCancellationRequested)
        {
            await Task.Delay(_options.SweepIntervalMilliseconds, stoppingToken);

            if (!leaderElection.IsLeader)
            {
                continue;
            }

            var now = timeProvider.GetUtcNow();
            await SweepOnceAsync(timeout, now, stoppingToken);
        }
    }

    public async Task<int> SweepOnceAsync(
        TimeSpan timeout,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var timedOut = await store.ClaimTimedOutAndResolveAsync(
                timeout,
                now,
                SweepBatchSize,
                (orderId, saga) => CreateTimeoutCommands(orderId, saga, now),
                ResolveWithinTransactionAsync,
                cancellationToken);
        foreach (var (orderId, saga) in timedOut)
        {
            SagaOrchestratorLog.SagaTimedOut(logger, orderId, saga.Step, _options.TimeoutSeconds, saga.CorrelationId);
        }

        return timedOut.Count;
    }

    /// <summary>
    /// Resolves a timed-out saga order.
    /// </summary>
    private Task ResolveWithinTransactionAsync(
        Guid orderId,
        SagaOrchestrationRecord saga,
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        CancellationToken cancellationToken)
    {
        switch (saga.Step)
        {
            case SagaStep.CommitInventory:
                return ResolveCommitInventoryWithinTransactionAsync(orderId, saga, connection, transaction, cancellationToken);

            default:
                return orderStatusStore.TryTransitionWithinTransactionAsync(
                    connection, transaction, orderId, OrderStatuses.Cancelled, saga.CorrelationId, cancellationToken);
        }
    }

    private async Task ResolveCommitInventoryWithinTransactionAsync(
        Guid orderId,
        SagaOrchestrationRecord saga,
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        CancellationToken cancellationToken)
    {
        await orderStatusStore.TryTransitionWithinTransactionAsync(
            connection, transaction, orderId, OrderStatuses.Confirmed, saga.CorrelationId, cancellationToken);
        await orderStatusStore.TryTransitionWithinTransactionAsync(
            connection, transaction, orderId, OrderStatuses.FulfillmentHold, saga.CorrelationId, cancellationToken);
    }

    private List<SagaOutboxCommand> CreateTimeoutCommands(
        Guid orderId,
        SagaOrchestrationRecord saga,
        DateTimeOffset occurredAt)
    {
        if (saga.Step is not (SagaStep.ReserveInventory or SagaStep.DecidePayment or SagaStep.ReleaseInventory))
        {
            return [];
        }

        var commands = new List<SagaOutboxCommand>(saga.Lines.Count);
        foreach (var line in saga.Lines)
        {
            var request = new InventoryReservationReleaseRequested(
                line.ReservationId,
                orderId,
                line.Sku,
                line.Quantity,
                saga.CorrelationId,
                occurredAt);
            commands.Add(SagaOutboxCommand.Create(
                orderId,
                _options.ReleaseRequestedTopic,
                line.Sku,
                request,
                saga.CorrelationId,
                occurredAt));
            SagaOrchestratorLog.TimeoutReleaseRequested(logger, orderId, line.Sku, saga.CorrelationId);
        }

        return commands;
    }
}
