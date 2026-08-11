using BuildingBlocks;
using Microsoft.Extensions.Options;

namespace Orders.Worker;

/// <summary>
/// The explicit compensation half of the choreography-vs-orchestration comparison - the
/// choreographed saga has no equivalent. If Payments.Service or
/// Inventory.Service never replies, the orchestrator itself notices and
/// resolves the order instead of leaving it parked at "Created" forever.
/// Gated on LeaderElectionService.IsLeader, so every replica
/// runs this loop but only the leader acts. Resolving now
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
        var timedOut = await store.ClaimTimedOutAndQueueAsync(
                timeout,
                now,
                SweepBatchSize,
                (orderId, saga) => CreateTimeoutCommands(orderId, saga, now),
                cancellationToken);
        foreach (var (orderId, saga) in timedOut)
        {
            SagaOrchestratorLog.SagaTimedOut(logger, orderId, saga.Step, _options.TimeoutSeconds, saga.CorrelationId);
            await ResolveAsync(orderId, saga, cancellationToken);
        }

        return timedOut.Count;
    }

    /// <summary>Public so integration tests can drive it directly, the same shape as the other saga classes' testable seams.</summary>
    public async Task ResolveAsync(Guid orderId, SagaOrchestrationRecord saga, CancellationToken cancellationToken)
    {
        switch (saga.Step)
        {
            case SagaStep.DecidePayment:
            case SagaStep.ReleaseInventory:
                // Every line's reservation certainly exists and certainly
                // hasn't been committed - DecidePayment is only reached
                // once every line replied Reserved, and
                // ReleaseInventory means a release was already requested
                // once for every line, so resending is a safe redelivery,
                // not a guess.
                await orderStatusStore.TryCancelAsync(orderId, saga.CorrelationId, cancellationToken);
                break;

            case SagaStep.CommitInventory:
                // Payment was approved to reach this step, so the order is
                // real - but whether the commit landed is unknown, so this
                // does not guess. FulfillmentHold is the same "needs a
                // human" state HandleCommitRepliedAsync reaches on an
                // explicit Committed:false reply; a timeout is just a
                // reply that never arrived to say either way.
                await orderStatusStore.TryConfirmAsync(orderId, saga.CorrelationId, cancellationToken);
                await orderStatusStore.TryTransitionAsync(orderId, OrderStatuses.FulfillmentHold, saga.CorrelationId, cancellationToken);
                break;

            default:
                // ReserveInventory is cancelled after the durable release
                // command was queued while claiming the timeout.
                await orderStatusStore.TryCancelAsync(orderId, saga.CorrelationId, cancellationToken);
                break;
        }
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
