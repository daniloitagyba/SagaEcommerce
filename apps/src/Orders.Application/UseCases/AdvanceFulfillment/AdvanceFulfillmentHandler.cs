using BuildingBlocks;
using Microsoft.Extensions.Logging;
using Orders.Application.Ports;

namespace Orders.Application.UseCases.AdvanceFulfillment;

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
    IOrderCache orderCache,
    ILogger<AdvanceFulfillmentHandler> logger)
{
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
}

public sealed partial class FulfillmentLog
{
    [LoggerMessage(EventId = 1200, Level = LogLevel.Information, Message = "Advanced order {OrderId} to {Status} with correlation {CorrelationId}")]
    public static partial void Advanced(ILogger logger, Guid orderId, string status, string correlationId);

    [LoggerMessage(EventId = 1201, Level = LogLevel.Warning, Message = "Refused to move order {OrderId} to {Status} - it is not in a state that move can be made from (correlation {CorrelationId})")]
    public static partial void Refused(ILogger logger, Guid orderId, string status, string correlationId);
}
