using BuildingBlocks;
using Microsoft.Extensions.Options;

namespace Orders.Worker;

/// <summary>
/// The explicit compensation half of Milestone 22's comparison - the
/// choreographed saga has no equivalent. If Payments.Service never
/// replies, the orchestrator itself notices and cancels the order.
/// Milestone 36: gated on LeaderElectionService.IsLeader, so every replica
/// runs this loop but only the leader acts. Milestone 69: cancelling now
/// also releases the coupon redemption and voids any card hold, since
/// those hang off the transition. Still out of scope: releasing an
/// <em>inventory</em> reservation held by the timed-out step - that needs
/// a compensating command per step, not a status change.
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
