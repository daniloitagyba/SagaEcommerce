using System.Text.Json;
using BuildingBlocks;
using Confluent.Kafka;
using Microsoft.Extensions.Options;

namespace Orders.Worker;

/// <summary>
/// The explicit compensation half of Milestone 22's comparison - the
/// choreographed saga has no equivalent. If Payments.Service or
/// Inventory.Service never replies, the orchestrator itself notices and
/// resolves the order instead of leaving it parked at "Created" forever.
/// Milestone 36: gated on LeaderElectionService.IsLeader, so every replica
/// runs this loop but only the leader acts. Milestone 69: resolving now
/// also releases the coupon redemption and voids any card hold, since
/// those hang off the transition.
///
/// <para>
/// Milestone 77: releases the <em>inventory</em> reservation too, but only
/// for the two steps where that's provably safe. A blind release for every
/// timed-out step was the original plan and turned out to be unsafe:
/// Inventory.Service's settlement path (<c>WarehouseAllocationStore.
/// TrySettleReservationAsync</c>) falls back to mutating the aggregate
/// <c>InventoryItem</c> row directly whenever it finds no per-reservation
/// allocation record - a deliberate M72 migration compatibility path, but
/// one that can't tell "this reservation genuinely never happened" apart
/// from "this reservation predates per-warehouse tracking." Releasing a
/// reservation that never happened would decrement a real concurrent
/// order's <c>ReservedQuantity</c> and conjure phantom <c>AvailableQuantity</c>
/// - worse than the gap this milestone closes.
/// </para>
/// <para>
/// So: <see cref="SagaStep.DecidePayment"/> and
/// <see cref="SagaStep.ReleaseInventory"/> get an explicit release (the
/// reservation is certain to exist and certain not to be committed yet -
/// the same guarantee <c>HandlePaymentDecisionRepliedAsync</c>'s declined
/// branch already relies on). <see cref="SagaStep.CommitInventory"/> gets
/// <see cref="OrderStatuses.FulfillmentHold"/> instead - payment was
/// already approved, so the order is real, but whether the commit itself
/// landed is genuinely unknown from here, and guessing wrong in either
/// direction either loses inventory or corrupts someone else's count.
/// <see cref="SagaStep.ReserveInventory"/> still gets a plain cancel, no
/// release attempted - whether anything was ever reserved is unknown, and
/// that is the one gap this milestone leaves open rather than paper over.
/// </para>
/// <para>
/// Milestone 78: a saga can now have several lines in flight at once, so
/// every action above that used to touch "the" reservation now loops over
/// <see cref="SagaOrchestrationRecord.Lines"/> instead - releasing every
/// line on a DecidePayment/ReleaseInventory timeout, not just one.
/// </para>
/// </summary>
public sealed class SagaTimeoutSweeper(
    IOptions<SagaOrchestrationOptions> options,
    IProducer<string, string> producer,
    SagaOrchestrationStore store,
    OrderStatusStore orderStatusStore,
    LeaderElectionService leaderElection,
    ILogger<SagaTimeoutSweeper> logger) : BackgroundService
{
    private const int SweepBatchSize = 100;
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);
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
                await ResolveAsync(orderId, saga, stoppingToken);
            }
        }
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
                // once every line replied Reserved (Milestone 78), and
                // ReleaseInventory means a release was already requested
                // once for every line, so resending is a safe redelivery,
                // not a guess.
                foreach (var line in saga.Lines)
                {
                    await PublishReleaseAsync(orderId, saga.CorrelationId, line, cancellationToken);
                }

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
                // ReserveInventory: unknown whether anything was ever
                // reserved. See the class comment for why releasing here
                // is not safe with today's Inventory.Service fallback.
                await orderStatusStore.TryCancelAsync(orderId, saga.CorrelationId, cancellationToken);
                break;
        }
    }

    private async Task PublishReleaseAsync(Guid orderId, string correlationId, SagaLineRecord line, CancellationToken cancellationToken)
    {
        var request = new InventoryReservationReleaseRequested(
            line.ReservationId, orderId, line.Sku, line.Quantity, correlationId, DateTimeOffset.UtcNow);

        var message = new Message<string, string>
        {
            Key = line.Sku,
            Value = JsonSerializer.Serialize(request, SerializerOptions)
        };

        try
        {
            await producer.ProduceAsync(_options.ReleaseRequestedTopic, message, cancellationToken);
            SagaOrchestratorLog.TimeoutReleaseRequested(logger, orderId, line.Sku, correlationId);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            SagaOrchestratorLog.NextStepPublishFailed(logger, orderId, exception);
        }
    }
}
