using BuildingBlocks;
using Microsoft.Extensions.Options;
using Npgsql;

namespace Orders.Worker;

/// <summary>
/// The explicit compensation half of the choreography-vs-orchestration comparison - the
/// choreographed saga has no equivalent. If Payments.Service or
/// Inventory.Service never replies, the orchestrator itself notices and
/// resolves the order instead of leaving it parked at "Created" forever.
/// Gated on LeaderElectionService.IsLeader, so every replica
/// runs this loop but only the leader acts - an optimization, not the
/// correctness mechanism: ClaimTimedOutAndResolveAsync's own FOR UPDATE
/// SKIP LOCKED is what actually makes a double-sweep (two replicas acting
/// during a brief leadership handoff, since IsLeader carries no fencing
/// token) safe, by construction, the same way SKIP LOCKED already lets a
/// second outbox poller safely coexist with the first. Resolving now
/// also releases the coupon redemption and voids any card hold, since
/// those hang off the transition.
///
/// <para>
/// Releases the <em>inventory</em> reservation for every step before commit.
/// Inventory settles only a recorded per-reservation allocation, so a
/// release for a reservation that never landed is an idempotent failed
/// reply and cannot mutate another order's stock. This also compensates the
/// important case where reserve succeeded but its reply was lost.
/// </para>
/// <para>
/// So: <see cref="SagaStep.ReserveInventory"/>,
/// <see cref="SagaStep.DecidePayment"/> and
/// <see cref="SagaStep.ReleaseInventory"/> get an explicit release.
/// <see cref="SagaStep.CommitInventory"/> gets
/// <see cref="OrderStatuses.FulfillmentHold"/> instead - payment was
/// already approved, so the order is real, but whether the commit itself
/// landed is genuinely unknown from here, and guessing wrong in either
/// direction either loses inventory or corrupts someone else's count.
/// </para>
/// <para>
/// A saga can now have several lines in flight at once, so
/// every action above that used to touch "the" reservation now loops over
/// <see cref="SagaOrchestrationRecord.Lines"/> instead - releasing every
/// line on a DecidePayment/ReleaseInventory timeout, not just one.
/// </para>
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
        // ClaimTimedOutAndResolveAsync (not the undecorated
        // ClaimTimedOutAndQueueAsync this used to call) folds each order's
        // status resolution into the very transaction that deletes its
        // saga row - see that method's own comment for why a separate,
        // later call used to leave orders permanently stranded on a crash.
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
    /// Resolves a single timed-out order's own status, against the exact
    /// connection/transaction ClaimTimedOutAndResolveAsync is about to
    /// commit alongside the saga row's deletion - see that method's class
    /// comment. Same decision table this class always used (Public so
    /// integration tests can drive it directly - see
    /// SagaOrchestrationStoreTests/SagaTimeoutSweeperTests for the
    /// per-step assertions), just applied atomically now instead of after
    /// the fact.
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
                // Payment was approved to reach this step, so the order is
                // real - but whether the commit landed is unknown, so this
                // does not guess. FulfillmentHold is the same "needs a
                // human" state HandleCommitRepliedAsync reaches on an
                // explicit Committed:false reply; a timeout is just a
                // reply that never arrived to say either way.
                return ResolveCommitInventoryWithinTransactionAsync(orderId, saga, connection, transaction, cancellationToken);

            default:
                // DecidePayment and ReleaseInventory: every line's
                // reservation certainly exists and certainly hasn't been
                // committed - DecidePayment is only reached once every
                // line replied Reserved, and ReleaseInventory means a
                // release was already requested once for every line, so
                // resending is a safe redelivery, not a guess.
                // ReserveInventory (the fallthrough default): cancelled
                // after the durable release command was queued while
                // claiming the timeout, same transaction.
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
