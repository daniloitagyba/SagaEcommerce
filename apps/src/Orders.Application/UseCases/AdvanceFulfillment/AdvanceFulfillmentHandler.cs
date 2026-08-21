using BuildingBlocks;
using Microsoft.Extensions.Logging;
using Orders.Application.Ports;

namespace Orders.Application.UseCases.AdvanceFulfillment;

using Orders.Application;

public enum AdvanceFulfillmentOutcome
{
    Advanced,
    IllegalTransition,
    NotApplicable,
    NotFound
}

public sealed record AdvanceFulfillmentResult(AdvanceFulfillmentOutcome Outcome, string? Status);

/// <summary>Moves an order through the fulfilment states an external actor drives.</summary>
public sealed class AdvanceFulfillmentHandler(
    IOrderStatusRepository repository,
    IOrderRepository orderRepository,
    IOrderCache orderCache,
    ILogger<AdvanceFulfillmentHandler> logger)
{
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

    public async Task<AdvanceFulfillmentResult> HandleSelfServiceCancelAsync(
        Guid orderId,
        CallerIdentity caller,
        string correlationId,
        CancellationToken cancellationToken)
    {
        var order = await orderRepository.FindByIdAsync(orderId, cancellationToken);

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
