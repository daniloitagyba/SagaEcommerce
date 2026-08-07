using System.Text.Json;
using BuildingBlocks;
using Microsoft.EntityFrameworkCore;
using Orders.Application.Exceptions;
using Orders.Application.Ports;
using Orders.Infrastructure.Data;
using Polly;
using Polly.Registry;

namespace Orders.Infrastructure.Persistence;

public sealed class EfOrderStatusRepository(
    OrdersDbContext dbContext,
    ResiliencePipelineProvider<string> pipelineProvider) : IOrderStatusRepository
{
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);
    private readonly ResiliencePipeline _pipeline = pipelineProvider.GetPipeline(ResilienceExtensions.PostgresPipeline);

    public async Task<OrderTransition> TryTransitionAsync(
        Guid orderId,
        string targetStatus,
        IReadOnlyList<string> allowedFrom,
        OrderSettlementAction settlementAction,
        string correlationId,
        CancellationToken cancellationToken)
    {
        try
        {
            return await _pipeline.ExecuteAsync(async ct =>
            {
                await using var transaction = await dbContext.Database.BeginTransactionAsync(ct);

                // Same guarded CAS as Orders.Worker's OrderStatusStore, for
                // the same reason: `status = ANY(...)` decides both "is this
                // move legal from where the row actually is?" and "am I the
                // one making it?" in a single statement, so two operators
                // racing to ship the same order cannot both win.
                var order = await dbContext.Orders
                    .Where(item => item.Id == orderId && allowedFrom.Contains(item.Status))
                    .Select(item => new { item.PaymentMethod })
                    .FirstOrDefaultAsync(ct);

                if (order is null)
                {
                    // Distinguish "no such order" from "wrong state" - the
                    // caller turns them into 404 and 409 respectively, and
                    // conflating them would tell an operator their order
                    // does not exist when it merely already shipped.
                    var exists = await dbContext.Orders.AnyAsync(item => item.Id == orderId, ct);
                    await transaction.RollbackAsync(ct);
                    return new OrderTransition(
                        exists ? OrderTransitionOutcome.NotApplicable : OrderTransitionOutcome.NotFound,
                        null);
                }

                var updated = await dbContext.Database.ExecuteSqlInterpolatedAsync(
                    $"""
                    UPDATE orders SET status = {targetStatus}
                    WHERE id = {orderId} AND status = ANY({allowedFrom.ToArray()})
                    """,
                    ct);

                if (updated == 0)
                {
                    // Lost the race between the read above and this write.
                    await transaction.RollbackAsync(ct);
                    return new OrderTransition(OrderTransitionOutcome.NotApplicable, null);
                }

                // Milestone 69: a cancellation here must give the coupon
                // slot back, exactly as the saga-driven one does.
                //
                // Orders.Worker settles the redemption after its status
                // commit; this path can do better because the coupon lives
                // in the same database as the order, so the release rides
                // the same transaction. A cancelled order that kept its
                // redemption would spend a slot forever on an order that no
                // longer exists.
                if (targetStatus == OrderStatuses.Cancelled)
                {
                    await ReleaseCouponAsync(orderId, ct);
                }

                // Queued in the same transaction as the status change: a
                // capture command that outlived a rolled-back "Shipped"
                // would charge a customer for goods that never left.
                if (settlementAction != OrderSettlementAction.None
                    && PaymentMethods.RequiresCapture(order.PaymentMethod))
                {
                    QueueSettlementCommand(orderId, settlementAction, targetStatus, correlationId);
                    await dbContext.SaveChangesAsync(ct);
                }

                await transaction.CommitAsync(ct);
                return new OrderTransition(OrderTransitionOutcome.Advanced, order.PaymentMethod);
            }, cancellationToken);
        }
        catch (Exception exception) when (ResilienceExtensions.IsInfrastructureFault(exception))
        {
            throw new InfrastructureUnavailableException("PostgreSQL is currently unavailable.", exception);
        }
    }

    /// <summary>
    /// Same guard as Orders.Worker's CouponRedemptionStore: releasable from
    /// Reserved or Confirmed, never from Released, so a slot is handed back
    /// exactly once no matter how many times a cancellation is retried.
    /// </summary>
    private async Task ReleaseCouponAsync(Guid orderId, CancellationToken cancellationToken)
    {
        await dbContext.Database.ExecuteSqlInterpolatedAsync(
            $"""
            WITH released AS (
                UPDATE coupon_redemptions
                SET state = {CouponRedemptionState.Released}, settled_at = {DateTimeOffset.UtcNow}
                WHERE order_id = {orderId}
                  AND state IN ({CouponRedemptionState.Reserved}, {CouponRedemptionState.Confirmed})
                RETURNING code
            )
            UPDATE coupons
            SET redemption_count = GREATEST(redemption_count - 1, 0)
            FROM released
            WHERE coupons.code = released.code
            """,
            cancellationToken);
    }

    private void QueueSettlementCommand(
        Guid orderId,
        OrderSettlementAction action,
        string targetStatus,
        string correlationId)
    {
        var now = DateTimeOffset.UtcNow;

        var (eventType, payload) = action == OrderSettlementAction.Capture
            ? (nameof(PaymentCaptureRequested),
               JsonSerializer.Serialize(new PaymentCaptureRequested(orderId, correlationId, now), SerializerOptions))
            : (nameof(PaymentVoidRequested),
               JsonSerializer.Serialize(new PaymentVoidRequested(orderId, $"order {targetStatus.ToLowerInvariant()}", correlationId, now), SerializerOptions));

        dbContext.OutboxMessages.Add(OutboxMessage.Create(
            Guid.NewGuid(),
            eventType,
            payload,
            now,
            correlationId,
            System.Diagnostics.Activity.Current?.Id,
            System.Diagnostics.Activity.Current?.TraceStateString));
    }
}
