using BuildingBlocks;
using Npgsql;
using Orders.Application.Ports;
using Orders.Infrastructure.Persistence;
using Polly.Registry;

namespace Orders.Worker;

public enum StatusTransitionResult
{
    Transitioned,
    NotApplicable,
    IllegalTransition
}

public sealed class OrderStatusStore
{
    private readonly OrderTransitionExecutor _executor;

    public OrderStatusStore(
        NpgsqlDataSource dataSource,
        CouponRedemptionStore couponRedemptionStore,
        PromotionCampaignStore promotionCampaignStore,
        PaymentSettlementRequester settlementRequester,
        CustomerTierStore customerTierStore,
        ResiliencePipelineProvider<string> pipelineProvider)
    {
        _executor = new OrderTransitionExecutor(dataSource, pipelineProvider);
    }

    public async Task<bool> TryConfirmAsync(Guid orderId, string correlationId, CancellationToken cancellationToken) =>
        (await TransitionAsync(orderId, OrderStatuses.Confirmed, correlationId, cancellationToken)) == StatusTransitionResult.Transitioned;

    public async Task<bool> TryCancelAsync(Guid orderId, string correlationId, CancellationToken cancellationToken) =>
        (await TransitionAsync(orderId, OrderStatuses.Cancelled, correlationId, cancellationToken)) == StatusTransitionResult.Transitioned;

    public Task<StatusTransitionResult> TryTransitionAsync(Guid orderId, string targetStatus, string correlationId, CancellationToken cancellationToken) =>
        TransitionAsync(orderId, targetStatus, correlationId, cancellationToken);

    public async Task<bool> TryTransitionWithinTransactionAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid orderId,
        string targetStatus,
        string correlationId,
        CancellationToken cancellationToken)
    {
        var allowedFrom = OrderStatuses.PredecessorsOf(targetStatus);
        if (allowedFrom.Count == 0)
        {
            return false;
        }

        var result = await _executor.TryTransitionWithinTransactionAsync(
            connection, transaction, orderId, targetStatus, allowedFrom, correlationId,
            includeCancellationCompensation: false, cancellationToken);
        return result.Outcome == OrderTransitionOutcome.Advanced;
    }

    private async Task<StatusTransitionResult> TransitionAsync(Guid orderId, string targetStatus, string correlationId, CancellationToken cancellationToken)
    {
        var allowedFrom = OrderStatuses.PredecessorsOf(targetStatus);
        if (allowedFrom.Count == 0)
        {
            return StatusTransitionResult.IllegalTransition;
        }

        var result = await _executor.TryTransitionAsync(
            orderId, targetStatus, allowedFrom, correlationId,
            includeCancellationCompensation: false, cancellationToken);
        return result.Outcome == OrderTransitionOutcome.Advanced
            ? StatusTransitionResult.Transitioned
            : StatusTransitionResult.NotApplicable;
    }
}
