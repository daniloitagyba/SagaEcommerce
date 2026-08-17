using System.Diagnostics;
using System.Text.Json;
using BuildingBlocks;
using Confluent.Kafka;
using Inventory.Service.Data;
using Microsoft.EntityFrameworkCore;

namespace Inventory.Service;

public sealed partial class InventoryReservationMessageProcessor
{
    /// <summary>Releases backorders for a SKU in FIFO order of attempt, skipping any that cannot yet be filled rather than blocking smaller orders behind them.</summary>
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
                continue;
            }

            dbContext.Backorders.Remove(backorder);

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

    /// <summary>Deletes every backorder for a cancelled order; idempotent, and no reply is published since nothing is waiting on one.</summary>
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

        var processedAt = _timeProvider.GetUtcNow();
        var inboxConsumerName = $"{_kafkaOptions.ConsumerGroup}-backorder-cancel";
        var insertedRows = await InboxStore.TryRecordWithinTransactionAsync(
            dbContext.Database, inboxConsumerName, request.OrderId,
            consumeResult.Topic, consumeResult.Partition.Value, consumeResult.Offset.Value,
            correlationId, processedAt, cancellationToken);

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
