using BuildingBlocks;
using Npgsql;
using NpgsqlTypes;
using Polly;
using Polly.Registry;

namespace Orders.Worker;

public enum StatusTransitionResult
{
    /// <summary>The row moved, and this caller is the one that moved it.</summary>
    Transitioned,

    /// <summary>The move is legal in principle, but the row was not in an allowed state - already settled, or someone else won the race.</summary>
    NotApplicable,

    /// <summary>The move is not in the transition table at all.</summary>
    IllegalTransition
}

public sealed class OrderStatusStore(
    NpgsqlDataSource dataSource,
    CouponRedemptionStore couponRedemptionStore,
    PaymentSettlementRequester settlementRequester,
    CustomerTierStore customerTierStore,
    ResiliencePipelineProvider<string> pipelineProvider)
{
    // Milestone 69: `status = ANY(@allowed_from)` guards the whole set of
    // legal predecessors in one statement, since an order can now be
    // cancelled from four different states - read-then-write would
    // reintroduce the race the CAS exists to remove. RETURNING carries what
    // the follow-up actions need, so they fire for exactly the winner of
    // the compare-and-set and a loser cannot double-count.
    private const string UpdateSql = """
        UPDATE orders
        SET status = @status
        WHERE id = @id AND status = ANY(@allowed_from)
        RETURNING coupon_code, payment_method, customer_id, amount_cents;
        """;

    private readonly ResiliencePipeline _pipeline = pipelineProvider.GetPipeline(ResilienceExtensions.PostgresPipeline);

    public Task<bool> TryConfirmAsync(Guid orderId, string correlationId, CancellationToken cancellationToken)
        => TransitionOrFalseAsync(orderId, OrderStatuses.Confirmed, correlationId, cancellationToken);

    public Task<bool> TryCancelAsync(Guid orderId, string correlationId, CancellationToken cancellationToken)
        => TransitionOrFalseAsync(orderId, OrderStatuses.Cancelled, correlationId, cancellationToken);

    /// <summary>Milestone 69: the fulfilment states an operator or warehouse system drives.</summary>
    public Task<StatusTransitionResult> TryTransitionAsync(
        Guid orderId,
        string targetStatus,
        string correlationId,
        CancellationToken cancellationToken)
        => TransitionAsync(orderId, targetStatus, correlationId, cancellationToken);

    private async Task<bool> TransitionOrFalseAsync(
        Guid orderId,
        string targetStatus,
        string correlationId,
        CancellationToken cancellationToken)
        => await TransitionAsync(orderId, targetStatus, correlationId, cancellationToken) == StatusTransitionResult.Transitioned;

    private async Task<StatusTransitionResult> TransitionAsync(
        Guid orderId,
        string targetStatus,
        string correlationId,
        CancellationToken cancellationToken)
    {
        var allowedFrom = OrderStatuses.PredecessorsOf(targetStatus);
        if (allowedFrom.Count == 0)
        {
            return StatusTransitionResult.IllegalTransition;
        }

        var (transitioned, couponCode, paymentMethod, customerId, amount) = await _pipeline.ExecuteAsync(async ct =>
        {
            await using var command = dataSource.CreateCommand(UpdateSql);
            command.Parameters.AddWithValue("status", NpgsqlDbType.Varchar, targetStatus);
            command.Parameters.AddWithValue("id", NpgsqlDbType.Uuid, orderId);
            command.Parameters.AddWithValue("allowed_from", NpgsqlDbType.Array | NpgsqlDbType.Varchar, allowedFrom.ToArray());

            await using var reader = await command.ExecuteReaderAsync(ct);
            if (!await reader.ReadAsync(ct))
            {
                return (false, (string?)null, (string?)null, (string?)null, 0m);
            }

            return (
                true,
                await reader.IsDBNullAsync(0, ct) ? null : reader.GetString(0),
                await reader.IsDBNullAsync(1, ct) ? null : reader.GetString(1),
                await reader.IsDBNullAsync(2, ct) ? null : reader.GetString(2),
                reader.GetInt64(3) / 100m);
        }, cancellationToken);

        if (!transitioned)
        {
            return StatusTransitionResult.NotApplicable;
        }

        await RunSideEffectsAsync(orderId, targetStatus, correlationId, couponCode, paymentMethod, customerId, amount, cancellationToken);
        return StatusTransitionResult.Transitioned;
    }

    /// <summary>
    /// Runs after the status commit, not inside it: a redemption left
    /// Reserved is a recoverable discrepancy, but rolling back the order's
    /// own transition would undo something the rest of the saga already acted on.
    /// </summary>
    private async Task RunSideEffectsAsync(
        Guid orderId,
        string targetStatus,
        string correlationId,
        string? couponCode,
        string? paymentMethod,
        string? customerId,
        decimal amount,
        CancellationToken cancellationToken)
    {
        // Milestone 71: standing is earned on confirmation, not creation, or
        // placing and cancelling would be the cheapest route to a discount.
        if (targetStatus == OrderStatuses.Confirmed && customerId is not null)
        {
            await customerTierStore.RecordCompletedOrderAsync(customerId, amount, cancellationToken);
        }

        if (couponCode is not null)
        {
            if (targetStatus == OrderStatuses.Confirmed)
            {
                await couponRedemptionStore.TryConfirmAsync(couponCode, orderId, cancellationToken);
            }
            else if (targetStatus == OrderStatuses.Cancelled)
            {
                await couponRedemptionStore.TryReleaseAsync(couponCode, orderId, cancellationToken);
            }
        }

        // Only a card leaves money on hold; asking Payments to settle a Pix
        // payment is harmless but achieves nothing.
        if (paymentMethod is null || !PaymentMethods.RequiresCapture(paymentMethod))
        {
            return;
        }

        // Milestone 69 moved capture from Confirmed to Shipped - the whole point of a hold is taking the money when the goods actually leave.
        if (targetStatus == OrderStatuses.Shipped)
        {
            await settlementRequester.RequestCaptureAsync(orderId, correlationId, cancellationToken);
        }
        else if (targetStatus == OrderStatuses.Cancelled)
        {
            await settlementRequester.RequestVoidAsync(orderId, correlationId, "order cancelled", cancellationToken);
        }
    }
}
