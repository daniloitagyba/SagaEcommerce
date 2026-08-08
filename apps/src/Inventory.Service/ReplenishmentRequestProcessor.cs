using System.Diagnostics;
using System.Text.Json;
using BuildingBlocks;
using Confluent.Kafka;
using Inventory.Service.Data;
using Inventory.Service.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Inventory.Service;

/// <summary>
/// The consumer WarehouseReplenishmentNeeded never had.
/// Records a purchase order the moment a warehouse crosses its reorder
/// point; PurchaseOrderReceivingSweeper is the other half, turning it into
/// an actual restock once its lead time elapses.
/// </summary>
public sealed class ReplenishmentRequestProcessor(
    IServiceScopeFactory scopeFactory,
    IOptions<InventoryKafkaOptions> kafkaOptions,
    IOptions<ReplenishmentOptions> replenishmentOptions,
    ILogger<ReplenishmentRequestProcessor> logger)
{
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);
    private readonly InventoryKafkaOptions _kafkaOptions = kafkaOptions.Value;
    private readonly ReplenishmentOptions _replenishmentOptions = replenishmentOptions.Value;

    public async Task<MessageProcessingResult> ProcessAsync(
        ConsumeResult<string, string> consumeResult,
        CancellationToken cancellationToken)
    {
        WarehouseReplenishmentNeeded signal;
        try
        {
            signal = JsonSerializer.Deserialize<WarehouseReplenishmentNeeded>(consumeResult.Message.Value, SerializerOptions)
                ?? throw new JsonException("The request payload deserialized to null.");
        }
        catch (JsonException exception)
        {
            throw new InvalidReservationMessageException("The Kafka message is not a valid WarehouseReplenishmentNeeded event.", exception);
        }

        if (signal.EventId == Guid.Empty || string.IsNullOrWhiteSpace(signal.Sku) || string.IsNullOrWhiteSpace(signal.WarehouseCode))
        {
            throw new InvalidReservationMessageException("The event id, sku and warehouse code are required.");
        }

        using var activity = OrdersTelemetry.StartActivity(
            "inventory.replenishment_request",
            ActivityKind.Consumer,
            GetHeader(consumeResult.Message.Headers, MessagingHeaders.TraceParent),
            GetHeader(consumeResult.Message.Headers, MessagingHeaders.TraceState));
        activity?.SetTag("messaging.system", "kafka");
        activity?.SetTag("messaging.destination.name", consumeResult.Topic);
        activity?.SetTag("inventory.sku", signal.Sku);
        activity?.SetTag("inventory.warehouse_code", signal.WarehouseCode);
        activity?.SetTag("correlation.id", signal.CorrelationId);

        await using var scope = scopeFactory.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<InventoryDbContext>();

        await using var transaction = await dbContext.Database.BeginTransactionAsync(cancellationToken);

        var processedAt = DateTimeOffset.UtcNow;
        var inboxConsumerName = $"{_kafkaOptions.ConsumerGroup}-replenishment";
        var insertedRows = await dbContext.Database.ExecuteSqlInterpolatedAsync(
            $"""
            INSERT INTO inbox_messages (consumer_name, event_id, topic, partition, "offset", correlation_id, processed_at)
            VALUES ({inboxConsumerName}, {signal.EventId}, {consumeResult.Topic}, {consumeResult.Partition.Value}, {consumeResult.Offset.Value}, {signal.CorrelationId}, {processedAt})
            ON CONFLICT (consumer_name, event_id) DO NOTHING
            """,
            cancellationToken);

        if (insertedRows == 0)
        {
            await transaction.RollbackAsync(cancellationToken);
            OrdersTelemetry.RecordProcessed("duplicate");
            InventoryLog.Duplicate(logger, signal.EventId, inboxConsumerName);
            return MessageProcessingResult.Duplicate;
        }

        // Restocks to a multiple of the reorder point, not merely back to
        // it - see ReplenishmentOptions.TargetMultiplier.
        var target = signal.ReorderPoint * _replenishmentOptions.TargetMultiplier;
        var quantity = Math.Max(target - signal.AvailableQuantity, signal.ReorderPoint);

        var purchaseOrder = PurchaseOrder.Create(signal.Sku, signal.WarehouseCode, quantity, signal.CorrelationId, processedAt);
        dbContext.PurchaseOrders.Add(purchaseOrder);

        await dbContext.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        activity?.SetTag("inventory.purchase_order_id", purchaseOrder.Id);
        activity?.SetTag("inventory.purchase_order_quantity", quantity);
        OrdersTelemetry.RecordProcessed("success");
        ReplenishmentLog.Requested(logger, purchaseOrder.Id, signal.Sku, signal.WarehouseCode, quantity, signal.CorrelationId);
        return MessageProcessingResult.Processed;
    }

    private static string? GetHeader(Headers headers, string key)
    {
        var header = headers.LastOrDefault(item => string.Equals(item.Key, key, StringComparison.Ordinal));
        return header is null ? null : System.Text.Encoding.UTF8.GetString(header.GetValueBytes());
    }
}

public sealed partial class ReplenishmentLog
{
    [LoggerMessage(EventId = 9400, Level = LogLevel.Information, Message = "Purchase order {PurchaseOrderId} requested for {Quantity} of {Sku} at {WarehouseCode} (correlation {CorrelationId})")]
    public static partial void Requested(ILogger logger, Guid purchaseOrderId, string sku, string warehouseCode, int quantity, string correlationId);

    [LoggerMessage(EventId = 9401, Level = LogLevel.Information, Message = "Received purchase order {PurchaseOrderId}: {Quantity} of {Sku} landed at {WarehouseCode} (correlation {CorrelationId})")]
    public static partial void Received(ILogger logger, Guid purchaseOrderId, string sku, string warehouseCode, int quantity, string correlationId);

    [LoggerMessage(EventId = 9402, Level = LogLevel.Error, Message = "Purchase order receiving sweep failed; unreceived orders remain claimable next pass")]
    public static partial void ReceivingSweepFailed(ILogger logger, Exception exception);
}
