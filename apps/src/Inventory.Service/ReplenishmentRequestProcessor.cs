using System.Diagnostics;
using System.Text.Json;
using BuildingBlocks;
using Confluent.Kafka;
using Inventory.Service.Data;
using Inventory.Service.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Polly;
using Polly.Registry;

namespace Inventory.Service;

/// <summary>Records a purchase order the moment a warehouse crosses its reorder point.</summary>
public sealed class ReplenishmentRequestProcessor(
    IServiceScopeFactory scopeFactory,
    IOptions<InventoryKafkaOptions> kafkaOptions,
    IOptions<ReplenishmentOptions> replenishmentOptions,
    ILogger<ReplenishmentRequestProcessor> logger,
    ResiliencePipelineProvider<string> pipelineProvider)
{
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);
    private readonly InventoryKafkaOptions _kafkaOptions = kafkaOptions.Value;
    private readonly ReplenishmentOptions _replenishmentOptions = replenishmentOptions.Value;
    private readonly ResiliencePipeline _pipeline = pipelineProvider.GetPipeline(ResilienceExtensions.PostgresTransactionPipeline);

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

        return await _pipeline.ExecuteAsync(async ct =>
        {
            await using var transaction = await dbContext.Database.BeginTransactionAsync(ct);

            var processedAt = DateTimeOffset.UtcNow;
            var inboxConsumerName = $"{_kafkaOptions.ConsumerGroup}-replenishment";
            var insertedRows = await InboxStore.TryRecordWithinTransactionAsync(
                dbContext.Database, inboxConsumerName, signal.EventId,
                consumeResult.Topic, consumeResult.Partition.Value, consumeResult.Offset.Value,
                signal.CorrelationId, processedAt, ct);

            if (insertedRows == 0)
            {
                await transaction.RollbackAsync(ct);
                OrdersTelemetry.RecordProcessed("duplicate");
                InventoryLog.Duplicate(logger, signal.EventId, inboxConsumerName);
                return MessageProcessingResult.Duplicate;
            }

            var target = signal.ReorderPoint * _replenishmentOptions.TargetMultiplier;
            var quantity = Math.Max(target - signal.AvailableQuantity, signal.ReorderPoint);

            var purchaseOrder = PurchaseOrder.Create(signal.Sku, signal.WarehouseCode, quantity, signal.CorrelationId, processedAt);
            dbContext.PurchaseOrders.Add(purchaseOrder);

            await dbContext.SaveChangesAsync(ct);
            await transaction.CommitAsync(ct);

            activity?.SetTag("inventory.purchase_order_id", purchaseOrder.Id);
            activity?.SetTag("inventory.purchase_order_quantity", quantity);
            OrdersTelemetry.RecordProcessed("success");
            ReplenishmentLog.Requested(logger, purchaseOrder.Id, signal.Sku, signal.WarehouseCode, quantity, signal.CorrelationId);
            return MessageProcessingResult.Processed;
        }, cancellationToken);
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
