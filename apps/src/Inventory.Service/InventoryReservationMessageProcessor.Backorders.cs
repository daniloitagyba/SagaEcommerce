using System.Diagnostics;
using System.Text.Json;
using BuildingBlocks;
using Confluent.Kafka;
using Inventory.Service.Data;
using Microsoft.EntityFrameworkCore;

namespace Inventory.Service;

// The backorder-release path, split into its own file from
// InventoryReservationMessageProcessor.cs to stay under the 500-line
// module-size budget. Same partial class, same private members (logger,
// EnqueueReservationReply, EnqueueReplenishmentSignals) as the other part -
// this is a physical file split, not a different concern boundary.
public sealed partial class InventoryReservationMessageProcessor
{
    /// <summary>
    /// Strict FIFO. Jumping ahead to a later, smaller
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

    /// <summary>
    /// The order this backorder was waiting on behalf of was
    /// cancelled. A plain <c>DELETE ... WHERE order_id = @orderId</c> is
    /// idempotent by construction - no reservation was ever placed for a
    /// row that's still sitting here (see the class comment on
    /// <c>ReleaseBackordersAsync</c>'s Reserve call), so there is nothing
    /// to release, only the wait itself to stop. Deletes every line
    /// (an order can have several backorders) in one
    /// statement; no reply is published because nothing is waiting on one -
    /// the order is already Cancelled by the time this arrives.
    /// </summary>
    public async Task<MessageProcessingResult> ProcessBackorderCancellationAsync(
        ConsumeResult<string, string> consumeResult,
        CancellationToken cancellationToken)
    {
        BackorderCancellationRequested request;
        try
        {
            request = JsonSerializer.Deserialize<BackorderCancellationRequested>(consumeResult.Message.Value, SerializerOptions)
                ?? throw new JsonException("The request payload deserialized to null.");
        }
        catch (JsonException exception)
        {
            throw new InvalidReservationMessageException("The Kafka message is not a valid BackorderCancellationRequested event.", exception);
        }

        if (request.OrderId == Guid.Empty)
        {
            throw new InvalidReservationMessageException("The order identifier is required.");
        }

        var correlationId = GetHeader(consumeResult.Message.Headers, MessagingHeaders.CorrelationId) ?? request.CorrelationId;

        using var activity = OrdersTelemetry.StartActivity(
            "inventory.backorder_cancel",
            ActivityKind.Consumer,
            GetHeader(consumeResult.Message.Headers, MessagingHeaders.TraceParent),
            GetHeader(consumeResult.Message.Headers, MessagingHeaders.TraceState));
        activity?.SetTag("messaging.system", "kafka");
        activity?.SetTag("messaging.destination.name", consumeResult.Topic);
        activity?.SetTag("order.id", request.OrderId);
        activity?.SetTag("correlation.id", correlationId);

        await using var scope = scopeFactory.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<InventoryDbContext>();

        await using var transaction = await dbContext.Database.BeginTransactionAsync(cancellationToken);

        var processedAt = DateTimeOffset.UtcNow;
        var inboxConsumerName = $"{_kafkaOptions.ConsumerGroup}-backorder-cancel";
        var insertedRows = await dbContext.Database.ExecuteSqlInterpolatedAsync(
            $"""
            INSERT INTO inbox_messages (consumer_name, event_id, topic, partition, "offset", correlation_id, processed_at)
            VALUES ({inboxConsumerName}, {request.OrderId}, {consumeResult.Topic}, {consumeResult.Partition.Value}, {consumeResult.Offset.Value}, {correlationId}, {processedAt})
            ON CONFLICT (consumer_name, event_id) DO NOTHING
            """,
            cancellationToken);

        if (insertedRows == 0)
        {
            await transaction.RollbackAsync(cancellationToken);
            OrdersTelemetry.RecordProcessed("duplicate");
            InventoryLog.Duplicate(logger, request.OrderId, inboxConsumerName);
            return MessageProcessingResult.Duplicate;
        }

        var removed = await dbContext.Backorders
            .Where(backorder => backorder.OrderId == request.OrderId)
            .ExecuteDeleteAsync(cancellationToken);

        await dbContext.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        activity?.SetTag("inventory.backorders_cancelled", removed);
        OrdersTelemetry.RecordProcessed("success");
        InventoryLog.BackordersCancelled(logger, request.OrderId, removed, correlationId);
        return MessageProcessingResult.Processed;
    }
}
