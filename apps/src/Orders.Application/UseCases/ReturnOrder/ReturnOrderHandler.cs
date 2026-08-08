using BuildingBlocks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Orders.Application.Ports;
using Orders.Domain;

namespace Orders.Application.UseCases.ReturnOrder;

using Orders.Application; // CallerIdentity - a nested file-scoped namespace does not implicitly see its parent's types.

public sealed record ReturnItemRequest(string Sku, int Quantity);

public sealed record ReturnOrderResult(
    ReturnRejectionReason Rejection,
    string? OffendingSku,
    Guid? ReturnId,
    decimal RefundTotal,
    bool OrderFullyReturned,
    bool OrderNotFound);

/// <summary>
/// Accepts a partial return and works out what it is worth.
/// The refund arithmetic lives in the domain (Order.TryReturn ->
/// ReturnRefundCalculator), since it needs the order's own per-line
/// charged totals, which nothing outside the aggregate should recompute.
/// The regret window's length is the one piece of that
/// policy this handler supplies - everything else (whether this return
/// actually falls inside it) is decided in the domain, from the order's
/// own CreatedAt.
/// </summary>
public sealed class ReturnOrderHandler(
    IOrderReturnRepository repository,
    IOrderCache orderCache,
    IOptions<ReturnOptions> returnOptions,
    ILogger<ReturnOrderHandler> logger)
{
    public async Task<ReturnOrderResult> HandleAsync(
        Guid orderId,
        IReadOnlyList<ReturnItemRequest> items,
        string reason,
        ReturnReasonCategory reasonCategory,
        CallerIdentity caller,
        string correlationId,
        CancellationToken cancellationToken)
    {
        var order = await repository.FindForReturnAsync(orderId, cancellationToken);
        if (order is null)
        {
            return new ReturnOrderResult(ReturnRejectionReason.None, null, null, 0m, false, OrderNotFound: true);
        }

        // Checked before TryReturn mutates anything, not
        // after - the order is read either way to know whose it is, but a
        // non-owner's request must never reach the mutation, even
        // transiently. Reported the same as a genuinely missing order (see
        // OrderEndpoints.GetByIdAsync's identical reasoning) so probing an
        // id can't distinguish "not yours" from "doesn't exist."
        if (!caller.MayAccess(order.CustomerId))
        {
            return new ReturnOrderResult(ReturnRejectionReason.None, null, null, 0m, false, OrderNotFound: true);
        }

        var (orderReturn, rejection, offendingSku) = order.TryReturn(
            [.. items.Select(item => (item.Sku, item.Quantity))],
            string.IsNullOrWhiteSpace(reason) ? "customer return" : reason.Trim(),
            reasonCategory,
            TimeSpan.FromDays(returnOptions.Value.RegretWindowDays),
            DateTimeOffset.UtcNow);

        if (rejection != ReturnRejectionReason.None)
        {
            ReturnLog.Rejected(logger, orderId, rejection.ToString(), offendingSku ?? "-", correlationId);
            return new ReturnOrderResult(rejection, offendingSku, null, 0m, false, false);
        }

        var fullyReturned = order.IsFullyReturned;
        await repository.SaveReturnAsync(order, orderReturn!, fullyReturned, correlationId, cancellationToken);
        await orderCache.InvalidateAsync(orderId, cancellationToken);

        ReturnLog.Accepted(logger, orderId, orderReturn!.Id, orderReturn.RefundTotal, fullyReturned, correlationId);
        return new ReturnOrderResult(
            ReturnRejectionReason.None, null, orderReturn.Id, orderReturn.RefundTotal, fullyReturned, false);
    }
}

public sealed partial class ReturnLog
{
    [LoggerMessage(EventId = 1300, Level = LogLevel.Information, Message = "Accepted return {ReturnId} for order {OrderId} refunding {RefundTotal} (fullyReturned={FullyReturned}) correlation {CorrelationId}")]
    public static partial void Accepted(ILogger logger, Guid orderId, Guid returnId, decimal refundTotal, bool fullyReturned, string correlationId);

    [LoggerMessage(EventId = 1301, Level = LogLevel.Warning, Message = "Refused return for order {OrderId}: {Rejection} (sku {Sku}) correlation {CorrelationId}")]
    public static partial void Rejected(ILogger logger, Guid orderId, string rejection, string sku, string correlationId);
}
