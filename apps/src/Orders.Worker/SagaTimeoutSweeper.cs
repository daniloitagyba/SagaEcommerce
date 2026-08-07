using BuildingBlocks;
using Microsoft.Extensions.Options;

namespace Orders.Worker;

/// <summary>
/// The explicit compensation half of Milestone 22's comparison: the
/// choreographed saga has nothing equivalent to this. If Payments.Service
/// never replies, an order there just stays "Created" forever with no
/// automatic detection - here, the orchestrator itself owns noticing and
/// acting on that.
///
/// Milestone 36: gated on LeaderElectionService.IsLeader so only one
/// orders-worker replica actively sweeps at a time - every replica still
/// runs this loop, but non-leaders no-op each tick rather than each
/// redundantly scanning the same rows.
///
/// Milestone 43 scope note: a saga that times out mid-flight was logged and
/// dropped - the order stayed "Created" forever, which was honest about the
/// orchestrator noticing but useless to the customer looking at it.
///
/// Milestone 69 closes that half: a timed-out saga now cancels its order.
/// This became possible rather than merely desirable once OrderStatuses
/// existed, because cancelling from mid-flight means cancelling from
/// whichever state the order happens to be in, and the old CAS could only
/// guard against one hardcoded predecessor. Cancelling also releases the
/// coupon redemption and voids any card hold, since those hang off the
/// transition rather than off the caller.
///
/// Still out of scope, deliberately: releasing an <em>inventory</em>
/// reservation held by the timed-out step. That needs a compensating
/// command per step rather than a status change, and is the same piece of
/// work M43 deferred.
/// </summary>
public sealed class SagaTimeoutSweeper(
    IOptions<SagaOrchestrationOptions> options,
    SagaOrchestrationStore store,
    OrderStatusStore orderStatusStore,
    LeaderElectionService leaderElection,
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

            var timedOut = await store.ClaimTimedOutAsync(timeout, DateTimeOffset.UtcNow, SweepBatchSize, stoppingToken);
            foreach (var (orderId, saga) in timedOut)
            {
                SagaOrchestratorLog.SagaTimedOut(logger, orderId, saga.Step, _options.TimeoutSeconds, saga.CorrelationId);
                await orderStatusStore.TryCancelAsync(orderId, saga.CorrelationId, stoppingToken);
            }
        }
    }
}
