using BuildingBlocks;
using Microsoft.Extensions.Logging;
using Orders.Application.Ports;

namespace Orders.Application.UseCases.AdvanceFulfillment;

using Orders.Application; // CallerIdentity

public enum AdvanceFulfillmentOutcome
{
    Advanced,
    IllegalTransition,
    NotApplicable,
    NotFound
}

public sealed record AdvanceFulfillmentResult(AdvanceFulfillmentOutcome Outcome, string? Status);

/// <summary>
/// Milestone 69: moves an order through the fulfilment states an external
/// actor drives. Legality is decided from the transition table before
/// touching the database; whether the order is actually <em>in</em> a
/// state the move can be made from is decided by the compare-and-set
/// itself, the only answer that cannot be raced.
/// </summary>
public sealed class AdvanceFulfillmentHandler(
    IOrderStatusRepository repository,
    IOrderRepository orderRepository,
    IOrderCache orderCache,
    ILogger<AdvanceFulfillmentHandler> logger)
{
    /// <summary>
    /// Milestone 83: the states a shopper may cancel their own order from -
    /// a strict subset of OrderStatuses.PredecessorsOf(Cancelled). Picking
    /// and FulfillmentHold are deliberately excluded: the warehouse is
    /// already acting on the order, or a human already needs to look at
    /// it, and either way that call belongs to fulfilment
    /// (see FulfillmentEndpoints), not to the shopper who placed it.
    /// </summary>
    private static readonly string[] SelfServiceCancellableFrom =
        [OrderStatuses.Created, OrderStatuses.Confirmed, OrderStatuses.Backordered];

    public async Task<AdvanceFulfillmentResult> HandleAsync(
        Guid orderId,
        string targetStatus,
        string correlationId,
        CancellationToken cancellationToken)
    {
        var normalized = targetStatus.Trim();
        var allowedFrom = OrderStatuses.PredecessorsOf(normalized);

        if (allowedFrom.Count == 0)
        {
            return new AdvanceFulfillmentResult(AdvanceFulfillmentOutcome.IllegalTransition, null);
        }

        var transition = await repository.TryTransitionAsync(
            orderId,
            normalized,
            allowedFrom,
            OrderStatuses.SettlementActionFor(normalized),
            correlationId,
            cancellationToken);

        switch (transition.Outcome)
        {
            case OrderTransitionOutcome.Advanced:
                // Without this the order keeps reporting its old status for the whole cache TTL.
                await orderCache.InvalidateAsync(orderId, cancellationToken);
                FulfillmentLog.Advanced(logger, orderId, normalized, correlationId);
                return new AdvanceFulfillmentResult(AdvanceFulfillmentOutcome.Advanced, normalized);

            case OrderTransitionOutcome.NotFound:
                return new AdvanceFulfillmentResult(AdvanceFulfillmentOutcome.NotFound, null);

            default:
                FulfillmentLog.Refused(logger, orderId, normalized, correlationId);
                return new AdvanceFulfillmentResult(AdvanceFulfillmentOutcome.NotApplicable, null);
        }
    }

    /// <summary>
    /// Milestone 83: a shopper cancelling their own order - the self-service
    /// route this lab never had (fulfilment's POST .../fulfillment is the
    /// warehouse's endpoint, now Admin-gated). Ownership has to be checked
    /// before the compare-and-set runs, not on its result: CustomerId never
    /// changes once an order is created, so a plain read of it first carries
    /// none of the race risk Milestone 81 had to guard the order's
    /// <em>status</em> against.
    /// </summary>
    public async Task<AdvanceFulfillmentResult> HandleSelfServiceCancelAsync(
        Guid orderId,
        CallerIdentity caller,
        string correlationId,
        CancellationToken cancellationToken)
    {
        var order = await orderRepository.FindByIdAsync(orderId, cancellationToken);

        // Same order for both checks - hides "not yours" behind the same
        // NotFound a genuinely missing id produces, and refuses a
        // not-yet-cancellable order the same way an operator's illegal
        // request would, without leaking which of the two is true to
        // someone who was never entitled to know either.
        if (order is null || !caller.MayAccess(order.CustomerId))
        {
            return new AdvanceFulfillmentResult(AdvanceFulfillmentOutcome.NotFound, null);
        }

        if (!SelfServiceCancellableFrom.Contains(order.Status, StringComparer.Ordinal))
        {
            FulfillmentLog.Refused(logger, orderId, OrderStatuses.Cancelled, correlationId);
            return new AdvanceFulfillmentResult(AdvanceFulfillmentOutcome.NotApplicable, null);
        }

        var transition = await repository.TryTransitionAsync(
            orderId,
            OrderStatuses.Cancelled,
            SelfServiceCancellableFrom,
            OrderStatuses.SettlementActionFor(OrderStatuses.Cancelled),
            correlationId,
            cancellationToken);

        if (transition.Outcome != OrderTransitionOutcome.Advanced)
        {
            // Lost a race against something else moving the order in the
            // gap between the read above and the compare-and-set (e.g. the
            // saga confirming it) - not this shopper's fault, but not a
            // state this endpoint can move from any more either.
            FulfillmentLog.Refused(logger, orderId, OrderStatuses.Cancelled, correlationId);
            return new AdvanceFulfillmentResult(AdvanceFulfillmentOutcome.NotApplicable, null);
        }

        await orderCache.InvalidateAsync(orderId, cancellationToken);
        FulfillmentLog.Advanced(logger, orderId, OrderStatuses.Cancelled, correlationId);
        return new AdvanceFulfillmentResult(AdvanceFulfillmentOutcome.Advanced, OrderStatuses.Cancelled);
    }
}

public sealed partial class FulfillmentLog
{
    [LoggerMessage(EventId = 1200, Level = LogLevel.Information, Message = "Advanced order {OrderId} to {Status} with correlation {CorrelationId}")]
    public static partial void Advanced(ILogger logger, Guid orderId, string status, string correlationId);

    [LoggerMessage(EventId = 1201, Level = LogLevel.Warning, Message = "Refused to move order {OrderId} to {Status} - it is not in a state that move can be made from (correlation {CorrelationId})")]
    public static partial void Refused(ILogger logger, Guid orderId, string status, string correlationId);
}
