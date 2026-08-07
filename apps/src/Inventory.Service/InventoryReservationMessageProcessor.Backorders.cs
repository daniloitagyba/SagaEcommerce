using BuildingBlocks;
using Inventory.Service.Data;
using Microsoft.EntityFrameworkCore;

namespace Inventory.Service;

// Milestone 74: the backorder-release path, split into its own file from
// InventoryReservationMessageProcessor.cs to stay under the 500-line
// module-size budget. Same partial class, same private members (logger,
// EnqueueReservationReply, EnqueueReplenishmentSignals) as the other part -
// this is a physical file split, not a different concern boundary.
public sealed partial class InventoryReservationMessageProcessor
{
    /// <summary>
    /// Milestone 74: strict FIFO. Jumping ahead to a later, smaller
    /// backorder because it happens to fit would be unfair to whoever has
    /// been waiting the longest, so the loop stops at the first one that
    /// still cannot be filled rather than skipping past it - the rest wait
    /// for the next restock, same as they are waiting for this one.
    /// </summary>
    private async Task ReleaseBackordersAsync(
        InventoryDbContext dbContext,
        WarehouseAllocationStore allocationStore,
        string sku,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var pending = await dbContext.Backorders
            .Where(backorder => backorder.Sku == sku)
            .OrderBy(backorder => backorder.RequestedAt)
            .ToListAsync(cancellationToken);

        foreach (var backorder in pending)
        {
            var decision = await allocationStore.TryReserveAsync(
                backorder.ReservationId, sku, backorder.Quantity, now, cancellationToken);

            if (!decision.Reserved)
            {
                break;
            }

            dbContext.Backorders.Remove(backorder);

            // Reuses InventoryReservationReplied on the exact reservationId
            // the saga is still parked on - see OrderSagaReplyConsumer's
            // handling of Backordered:false. No new event type, no new
            // saga-side code: this looks like an ordinary late reply.
            var reply = new InventoryReservationReplied(
                backorder.ReservationId,
                backorder.OrderId,
                sku,
                backorder.Quantity,
                Reserved: true,
                Reason: null,
                backorder.CorrelationId,
                now);

            EnqueueReservationReply(dbContext, reply, now, backorder.CorrelationId);
            EnqueueReplenishmentSignals(dbContext, decision.CrossedReorderPoint, backorder.CorrelationId, now);

            InventoryLog.BackorderReleased(logger, backorder.ReservationId, sku, backorder.OrderId, backorder.CorrelationId);
        }
    }
}
