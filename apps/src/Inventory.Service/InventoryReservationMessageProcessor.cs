using System.Diagnostics;
using System.Text;
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

public enum MessageProcessingResult
{
    Processed,
    Duplicate
}

public sealed class InvalidReservationMessageException(string message, Exception? innerException = null)
    : Exception(message, innerException);

public sealed partial class InventoryReservationMessageProcessor(
    IServiceScopeFactory scopeFactory,
    IOptions<InventoryKafkaOptions> kafkaOptions,
    ILogger<InventoryReservationMessageProcessor> logger,
    ResiliencePipelineProvider<string> pipelineProvider,
    TimeProvider? timeProvider = null)
{
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);
    private readonly InventoryKafkaOptions _kafkaOptions = kafkaOptions.Value;
    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;
    private readonly ResiliencePipeline _pipeline = pipelineProvider.GetPipeline(ResilienceExtensions.PostgresTransactionPipeline);

    public async Task<MessageProcessingResult> ProcessAsync(
        ConsumeResult<string, string> consumeResult,
        CancellationToken cancellationToken)
    {
        var request = DeserializeAndValidate(consumeResult);
        var correlationId = GetHeader(consumeResult.Message.Headers, MessagingHeaders.CorrelationId)
            ?? request.CorrelationId;
        var traceParent = GetHeader(consumeResult.Message.Headers, MessagingHeaders.TraceParent);
        var traceState = GetHeader(consumeResult.Message.Headers, MessagingHeaders.TraceState);

        using var activity = OrdersTelemetry.StartActivity(
            "inventory.reserve",
            ActivityKind.Consumer,
            traceParent,
            traceState);
        activity?.SetTag("messaging.system", "kafka");
        activity?.SetTag("messaging.destination.name", consumeResult.Topic);
        activity?.SetTag("messaging.operation.type", "process");
        activity?.SetTag("inventory.sku", request.Sku);
        activity?.SetTag("inventory.reservation_id", request.ReservationId);
        activity?.SetTag("order.id", request.OrderId);
        activity?.SetTag("correlation.id", correlationId);

        using var logScope = logger.BeginScope(new Dictionary<string, object?>
        {
            ["CorrelationId"] = correlationId,
            ["ReservationId"] = request.ReservationId,
            ["Sku"] = request.Sku,
            ["OrderId"] = request.OrderId,
            ["TraceId"] = activity?.TraceId.ToString() ?? string.Empty
        });

        await using var scope = scopeFactory.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<InventoryDbContext>();

        return await _pipeline.ExecuteAsync(async ct =>
        {
            await using var transaction = await dbContext.Database.BeginTransactionAsync(ct);
            await SkuAdvisoryLock.AcquireAsync(dbContext, request.Sku, ct);

            var processedAt = _timeProvider.GetUtcNow();
            var insertedRows = await InboxStore.TryRecordWithinTransactionAsync(
                dbContext.Database, _kafkaOptions.ConsumerGroup, request.ReservationId,
                consumeResult.Topic, consumeResult.Partition.Value, consumeResult.Offset.Value,
                correlationId, processedAt, ct);

            if (insertedRows == 0)
            {
                await transaction.RollbackAsync(ct);
                OrdersTelemetry.RecordProcessed("duplicate");
                InventoryLog.Duplicate(logger, request.ReservationId, _kafkaOptions.ConsumerGroup);
                return MessageProcessingResult.Duplicate;
            }

            var allocationStore = scope.ServiceProvider.GetRequiredService<WarehouseAllocationStore>();
            var itemExists = await dbContext.InventoryItems.AnyAsync(i => i.Sku == request.Sku, ct);

            var decision = itemExists
                ? await allocationStore.TryReserveAsync(request.ReservationId, request.Sku, request.Quantity, processedAt, ct)
                : WarehouseAllocationStore.ReservationDecision.Refused;

            var backordered = !decision.Reserved && itemExists;
            if (backordered)
            {
                dbContext.Backorders.Add(Backorder.Create(
                    request.ReservationId, request.OrderId, request.Sku, request.Quantity, correlationId, processedAt));
                InventoryLog.Backordered(logger, request.ReservationId, request.Sku, correlationId);
            }

            var reason = !itemExists
                ? "unknown sku"
                : decision.Reserved ? null : "insufficient stock";

            var reply = new InventoryReservationReplied(
                request.ReservationId,
                request.OrderId,
                request.Sku,
                request.Quantity,
                decision.Reserved,
                reason,
                correlationId,
                processedAt,
                backordered);

            EnqueueReservationReply(dbContext, reply, processedAt, correlationId);
            EnqueueReplenishmentSignals(dbContext, decision.CrossedReorderPoint, correlationId, processedAt);

            await dbContext.SaveChangesAsync(ct);
            await transaction.CommitAsync(ct);

            activity?.SetTag("inventory.reserved", decision.Reserved);
            activity?.SetTag("inventory.backordered", backordered);
            OrdersTelemetry.RecordProcessed("success");
            InventoryLog.Decided(logger, request.ReservationId, request.Sku, decision.Reserved, correlationId);
            return MessageProcessingResult.Processed;
        }, cancellationToken);
    }

    private static void EnqueueReservationReply(
        InventoryDbContext dbContext, InventoryReservationReplied reply, DateTimeOffset processedAt, string correlationId)
    {
        dbContext.OutboxMessages.Add(OutboxMessage.Create(
            Guid.NewGuid(),
            nameof(InventoryReservationReplied),
            JsonSerializer.Serialize(reply, SerializerOptions),
            processedAt,
            correlationId,
            Activity.Current?.Id,
            Activity.Current?.TraceStateString));
    }

    /// <summary>Enqueues replenishment signals in the same transaction as the reservation that caused each crossing.</summary>
    private void EnqueueReplenishmentSignals(
        InventoryDbContext dbContext,
        IReadOnlyList<WarehouseStock> crossedReorderPoint,
        string correlationId,
        DateTimeOffset processedAt)
    {
        foreach (var stock in crossedReorderPoint)
        {
            var eventId = Guid.NewGuid();
            var signal = new WarehouseReplenishmentNeeded(
                eventId, stock.Sku, stock.WarehouseCode, stock.AvailableQuantity, stock.ReorderPoint, correlationId, processedAt);

            dbContext.OutboxMessages.Add(OutboxMessage.Create(
                eventId,
                nameof(WarehouseReplenishmentNeeded),
                JsonSerializer.Serialize(signal, SerializerOptions),
                processedAt,
                correlationId,
                Activity.Current?.Id,
                Activity.Current?.TraceStateString));

            InventoryLog.ReplenishmentNeeded(logger, signal.Sku, signal.WarehouseCode, signal.AvailableQuantity, signal.ReorderPoint);
        }
    }

    public async Task<MessageProcessingResult> ProcessCommitAsync(
        ConsumeResult<string, string> consumeResult,
        CancellationToken cancellationToken)
    {
        var request = DeserializeAndValidate<InventoryReservationCommitRequested>(
            consumeResult, r => (r.ReservationId, r.OrderId, r.Sku, r.Quantity));
        var correlationId = GetHeader(consumeResult.Message.Headers, MessagingHeaders.CorrelationId) ?? request.CorrelationId;

        return await ProcessSettlementAsync(
            consumeResult,
            request.ReservationId,
            request.OrderId,
            request.Sku,
            request.Quantity,
            correlationId,
            inboxConsumerSuffix: "commit",
            activityName: "inventory.commit",
            settleAllocation: true,
            commitAllocation: true,
            insufficientReason: "reservation not found or already settled",
            makeReply: (reservationId, orderId, sku, quantity, succeeded, reason, corrId, decidedAt) =>
                (nameof(InventoryReservationCommitReplied),
                    new InventoryReservationCommitReplied(reservationId, orderId, sku, quantity, succeeded, reason, corrId, decidedAt)),
            logDecided: (reservationId, sku, succeeded, corrId) =>
                InventoryLog.CommitDecided(logger, reservationId, sku, succeeded, corrId),
            cancellationToken);
    }

    public async Task<MessageProcessingResult> ProcessReleaseAsync(
        ConsumeResult<string, string> consumeResult,
        CancellationToken cancellationToken)
    {
        var request = DeserializeAndValidate<InventoryReservationReleaseRequested>(
            consumeResult, r => (r.ReservationId, r.OrderId, r.Sku, r.Quantity));
        var correlationId = GetHeader(consumeResult.Message.Headers, MessagingHeaders.CorrelationId) ?? request.CorrelationId;

        return await ProcessSettlementAsync(
            consumeResult,
            request.ReservationId,
            request.OrderId,
            request.Sku,
            request.Quantity,
            correlationId,
            inboxConsumerSuffix: "release",
            activityName: "inventory.release",
            settleAllocation: true,
            commitAllocation: false,
            insufficientReason: "reservation not found or already settled",
            makeReply: (reservationId, orderId, sku, quantity, succeeded, reason, corrId, decidedAt) =>
                (nameof(InventoryReservationReleaseReplied),
                    new InventoryReservationReleaseReplied(reservationId, orderId, sku, quantity, succeeded, reason, corrId, decidedAt)),
            logDecided: (reservationId, sku, succeeded, corrId) =>
                InventoryLog.ReleaseDecided(logger, reservationId, sku, succeeded, corrId),
            cancellationToken);
    }

    /// <summary>Returned units go back on the shelf; reuses the same inbox-dedup / mutate / reply shape as commit and release.</summary>
    public async Task<MessageProcessingResult> ProcessRestockAsync(
        ConsumeResult<string, string> consumeResult,
        CancellationToken cancellationToken)
    {
        var request = DeserializeAndValidate<InventoryRestockRequested>(
            consumeResult, r => (r.ReturnId, r.OrderId, r.Sku, r.Quantity));
        var correlationId = GetHeader(consumeResult.Message.Headers, MessagingHeaders.CorrelationId) ?? request.CorrelationId;
        var warehouseCode = request.WarehouseCode;

        return await ProcessSettlementAsync(
            consumeResult,
            request.ReturnId,
            request.OrderId,
            request.Sku,
            request.Quantity,
            correlationId,
            inboxConsumerSuffix: "restock",
            activityName: "inventory.restock",
            settleAllocation: false,
            commitAllocation: false,
            insufficientReason: "sku not found",
            makeReply: (returnId, orderId, sku, quantity, succeeded, reason, corrId, decidedAt) =>
                (nameof(InventoryRestockReplied),
                    new InventoryRestockReplied(returnId, orderId, sku, quantity, succeeded, corrId, decidedAt, warehouseCode)),
            logDecided: (returnId, sku, succeeded, corrId) =>
                InventoryLog.RestockDecided(logger, returnId, sku, succeeded, corrId),
            cancellationToken,
            warehouseCode);
    }

    private async Task<MessageProcessingResult> ProcessSettlementAsync(
        ConsumeResult<string, string> consumeResult,
        Guid reservationId,
        Guid orderId,
        string sku,
        int quantity,
        string correlationId,
        string inboxConsumerSuffix,
        string activityName,
        bool settleAllocation,
        bool commitAllocation,
        string insufficientReason,
        Func<Guid, Guid, string, int, bool, string?, string, DateTimeOffset, (string EventType, object Reply)> makeReply,
        Action<Guid, string, bool, string> logDecided,
        CancellationToken cancellationToken,
        string? warehouseCode = null)
    {
        var traceParent = GetHeader(consumeResult.Message.Headers, MessagingHeaders.TraceParent);
        var traceState = GetHeader(consumeResult.Message.Headers, MessagingHeaders.TraceState);

        using var activity = OrdersTelemetry.StartActivity(activityName, ActivityKind.Consumer, traceParent, traceState);
        activity?.SetTag("messaging.system", "kafka");
        activity?.SetTag("messaging.destination.name", consumeResult.Topic);
        activity?.SetTag("messaging.operation.type", "process");
        activity?.SetTag("inventory.sku", sku);
        activity?.SetTag("inventory.reservation_id", reservationId);
        activity?.SetTag("order.id", orderId);
        activity?.SetTag("correlation.id", correlationId);

        using var logScope = logger.BeginScope(new Dictionary<string, object?>
        {
            ["CorrelationId"] = correlationId,
            ["ReservationId"] = reservationId,
            ["Sku"] = sku,
            ["OrderId"] = orderId,
            ["TraceId"] = activity?.TraceId.ToString() ?? string.Empty
        });

        await using var scope = scopeFactory.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<InventoryDbContext>();

        return await _pipeline.ExecuteAsync(async ct =>
        {
            await using var transaction = await dbContext.Database.BeginTransactionAsync(ct);
            await SkuAdvisoryLock.AcquireAsync(dbContext, sku, ct);

            var processedAt = _timeProvider.GetUtcNow();
            var inboxConsumerName = $"{_kafkaOptions.ConsumerGroup}-{inboxConsumerSuffix}";
            var insertedRows = await InboxStore.TryRecordWithinTransactionAsync(
                dbContext.Database, inboxConsumerName, reservationId,
                consumeResult.Topic, consumeResult.Partition.Value, consumeResult.Offset.Value,
                correlationId, processedAt, ct);

            if (insertedRows == 0)
            {
                await transaction.RollbackAsync(ct);
                OrdersTelemetry.RecordProcessed("duplicate");
                InventoryLog.Duplicate(logger, reservationId, inboxConsumerName);
                return MessageProcessingResult.Duplicate;
            }

            var allocationStore = scope.ServiceProvider.GetRequiredService<WarehouseAllocationStore>();
            var succeeded = settleAllocation
                ? await allocationStore.TrySettleReservationAsync(reservationId, commitAllocation, processedAt, ct)
                : await allocationStore.TryRestockAsync(sku, quantity, processedAt, warehouseCode, ct);
            var reason = succeeded ? null : insufficientReason;

            await UpdateReservationLedgerAsync(
                allocationStore, succeeded, settleAllocation, commitAllocation,
                reservationId, orderId, sku, quantity, processedAt, ct);

            var (eventType, reply) = makeReply(reservationId, orderId, sku, quantity, succeeded, reason, correlationId, processedAt);
            var outboxMessage = OutboxMessage.Create(
                Guid.NewGuid(),
                eventType,
                JsonSerializer.Serialize(reply, SerializerOptions),
                processedAt,
                correlationId,
                Activity.Current?.Id,
                Activity.Current?.TraceStateString);

            dbContext.OutboxMessages.Add(outboxMessage);

            if (!settleAllocation && succeeded)
            {
                await ReleaseBackordersAsync(dbContext, allocationStore, sku, processedAt, ct);
            }

            await dbContext.SaveChangesAsync(ct);
            await transaction.CommitAsync(ct);

            activity?.SetTag("inventory.succeeded", succeeded);
            OrdersTelemetry.RecordProcessed("success");
            logDecided(reservationId, sku, succeeded, correlationId);
            return MessageProcessingResult.Processed;
        }, cancellationToken);
    }

    private static TRequest DeserializeAndValidate<TRequest>(
        ConsumeResult<string, string> consumeResult,
        Func<TRequest, (Guid ReservationId, Guid OrderId, string Sku, int Quantity)> extract)
    {
        TRequest request;
        try
        {
            request = JsonSerializer.Deserialize<TRequest>(consumeResult.Message.Value, SerializerOptions)
                ?? throw new JsonException("The request payload deserialized to null.");
        }
        catch (JsonException exception)
        {
            throw new InvalidReservationMessageException($"The Kafka message is not a valid {typeof(TRequest).Name} event.", exception);
        }

        var (reservationId, orderId, sku, quantity) = extract(request);
        if (reservationId == Guid.Empty || orderId == Guid.Empty)
        {
            throw new InvalidReservationMessageException("The reservation and order identifiers are required.");
        }

        if (string.IsNullOrWhiteSpace(sku) || quantity <= 0)
        {
            throw new InvalidReservationMessageException("A non-empty Sku and a positive Quantity are required.");
        }

        return request;
    }

    private static InventoryReservationRequested DeserializeAndValidate(ConsumeResult<string, string> consumeResult)
    {
        InventoryReservationRequested request;
        try
        {
            request = JsonSerializer.Deserialize<InventoryReservationRequested>(consumeResult.Message.Value, SerializerOptions)
                ?? throw new JsonException("The request payload deserialized to null.");
        }
        catch (JsonException exception)
        {
            throw new InvalidReservationMessageException("The Kafka message is not a valid InventoryReservationRequested event.", exception);
        }

        if (request.ReservationId == Guid.Empty || request.OrderId == Guid.Empty)
        {
            throw new InvalidReservationMessageException("The reservation and order identifiers are required.");
        }

        if (string.IsNullOrWhiteSpace(request.Sku) || request.Quantity <= 0)
        {
            throw new InvalidReservationMessageException("A non-empty Sku and a positive Quantity are required.");
        }

        return request;
    }

    private static string? GetHeader(Headers headers, string key)
    {
        var header = headers.LastOrDefault(item => string.Equals(item.Key, key, StringComparison.Ordinal));
        return header is null ? null : Encoding.UTF8.GetString(header.GetValueBytes());
    }
}
