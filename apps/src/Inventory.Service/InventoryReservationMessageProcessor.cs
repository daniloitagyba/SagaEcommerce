using System.Diagnostics;
using System.Text;
using System.Text.Json;
using BuildingBlocks;
using Confluent.Kafka;
using Inventory.Service.Data;
using Inventory.Service.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Inventory.Service;

public enum MessageProcessingResult
{
    Processed,
    Duplicate
}

public sealed class InvalidReservationMessageException(string message, Exception? innerException = null)
    : Exception(message, innerException);

public sealed class InventoryReservationMessageProcessor(
    IServiceScopeFactory scopeFactory,
    IOptions<InventoryKafkaOptions> kafkaOptions,
    ILogger<InventoryReservationMessageProcessor> logger)
{
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);
    private readonly InventoryKafkaOptions _kafkaOptions = kafkaOptions.Value;

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

        await using var transaction = await dbContext.Database.BeginTransactionAsync(cancellationToken);

        var processedAt = DateTimeOffset.UtcNow;
        var insertedRows = await dbContext.Database.ExecuteSqlInterpolatedAsync(
            $"""
            INSERT INTO inbox_messages (consumer_name, event_id, topic, partition, "offset", correlation_id, processed_at)
            VALUES ({_kafkaOptions.ConsumerGroup}, {request.ReservationId}, {consumeResult.Topic}, {consumeResult.Partition.Value}, {consumeResult.Offset.Value}, {correlationId}, {processedAt})
            ON CONFLICT (consumer_name, event_id) DO NOTHING
            """,
            cancellationToken);

        if (insertedRows == 0)
        {
            await transaction.RollbackAsync(cancellationToken);
            OrdersTelemetry.RecordProcessed("duplicate");
            InventoryLog.Duplicate(logger, request.ReservationId, _kafkaOptions.ConsumerGroup);
            return MessageProcessingResult.Duplicate;
        }

        // Deliberately a plain read-then-write, no SELECT ... FOR UPDATE.
        // Correctness for concurrent requests against the SAME Sku relies
        // entirely on Kafka never handing this Sku's partition to more than
        // one consumer instance at a time - see InventoryContracts.cs.
        var item = await dbContext.InventoryItems.FirstOrDefaultAsync(i => i.Sku == request.Sku, cancellationToken);
        var reserved = item is not null && item.TryReserve(request.Quantity, processedAt);
        var reason = item is null
            ? "unknown sku"
            : reserved ? null : "insufficient stock";

        var reply = new InventoryReservationReplied(
            request.ReservationId,
            request.OrderId,
            request.Sku,
            request.Quantity,
            reserved,
            reason,
            correlationId,
            processedAt);
        var outboxMessage = OutboxMessage.Create(
            Guid.NewGuid(),
            nameof(InventoryReservationReplied),
            JsonSerializer.Serialize(reply, SerializerOptions),
            processedAt,
            correlationId,
            Activity.Current?.Id,
            Activity.Current?.TraceStateString);

        dbContext.OutboxMessages.Add(outboxMessage);
        await dbContext.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        activity?.SetTag("inventory.reserved", reserved);
        OrdersTelemetry.RecordProcessed("success");
        InventoryLog.Decided(logger, request.ReservationId, request.Sku, reserved, correlationId);
        return MessageProcessingResult.Processed;
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
            mutate: (item, quantity, now) => item.TryCommit(quantity, now),
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
            mutate: (item, quantity, now) => item.TryRelease(quantity, now),
            insufficientReason: "reservation not found or already settled",
            makeReply: (reservationId, orderId, sku, quantity, succeeded, reason, corrId, decidedAt) =>
                (nameof(InventoryReservationReleaseReplied),
                    new InventoryReservationReleaseReplied(reservationId, orderId, sku, quantity, succeeded, reason, corrId, decidedAt)),
            logDecided: (reservationId, sku, succeeded, corrId) =>
                InventoryLog.ReleaseDecided(logger, reservationId, sku, succeeded, corrId),
            cancellationToken);
    }

    // Commit and Release are structurally identical to each other (and to
    // Reserve, minus the "unknown sku" case, since by this point the sku is
    // known to exist): inbox-dedup, mutate one InventoryItem, write one
    // outbox reply. Shared here since the only real difference is which
    // InventoryItem method to call and which reply type to build - the
    // transactional shape itself isn't worth re-deriving twice.
    private async Task<MessageProcessingResult> ProcessSettlementAsync(
        ConsumeResult<string, string> consumeResult,
        Guid reservationId,
        Guid orderId,
        string sku,
        int quantity,
        string correlationId,
        string inboxConsumerSuffix,
        string activityName,
        Func<InventoryItem, int, DateTimeOffset, bool> mutate,
        string insufficientReason,
        Func<Guid, Guid, string, int, bool, string?, string, DateTimeOffset, (string EventType, object Reply)> makeReply,
        Action<Guid, string, bool, string> logDecided,
        CancellationToken cancellationToken)
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

        await using var transaction = await dbContext.Database.BeginTransactionAsync(cancellationToken);

        var processedAt = DateTimeOffset.UtcNow;
        var inboxConsumerName = $"{_kafkaOptions.ConsumerGroup}-{inboxConsumerSuffix}";
        var insertedRows = await dbContext.Database.ExecuteSqlInterpolatedAsync(
            $"""
            INSERT INTO inbox_messages (consumer_name, event_id, topic, partition, "offset", correlation_id, processed_at)
            VALUES ({inboxConsumerName}, {reservationId}, {consumeResult.Topic}, {consumeResult.Partition.Value}, {consumeResult.Offset.Value}, {correlationId}, {processedAt})
            ON CONFLICT (consumer_name, event_id) DO NOTHING
            """,
            cancellationToken);

        if (insertedRows == 0)
        {
            await transaction.RollbackAsync(cancellationToken);
            OrdersTelemetry.RecordProcessed("duplicate");
            InventoryLog.Duplicate(logger, reservationId, inboxConsumerName);
            return MessageProcessingResult.Duplicate;
        }

        var item = await dbContext.InventoryItems.FirstOrDefaultAsync(i => i.Sku == sku, cancellationToken);
        var succeeded = item is not null && mutate(item, quantity, processedAt);
        var reason = succeeded ? null : insufficientReason;

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
        await dbContext.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        activity?.SetTag("inventory.succeeded", succeeded);
        OrdersTelemetry.RecordProcessed("success");
        logDecided(reservationId, sku, succeeded, correlationId);
        return MessageProcessingResult.Processed;
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

public sealed partial class InventoryLog
{
    [LoggerMessage(EventId = 9002, Level = LogLevel.Information, Message = "Decided reservation {ReservationId} for sku {Sku}: reserved={Reserved} with correlation {CorrelationId}")]
    public static partial void Decided(ILogger logger, Guid reservationId, string sku, bool reserved, string correlationId);

    [LoggerMessage(EventId = 9011, Level = LogLevel.Information, Message = "Decided commit {ReservationId} for sku {Sku}: committed={Committed} with correlation {CorrelationId}")]
    public static partial void CommitDecided(ILogger logger, Guid reservationId, string sku, bool committed, string correlationId);

    [LoggerMessage(EventId = 9012, Level = LogLevel.Information, Message = "Decided release {ReservationId} for sku {Sku}: released={Released} with correlation {CorrelationId}")]
    public static partial void ReleaseDecided(ILogger logger, Guid reservationId, string sku, bool released, string correlationId);

    [LoggerMessage(EventId = 9005, Level = LogLevel.Information, Message = "Skipped duplicate reservation {ReservationId} for consumer {ConsumerName}")]
    public static partial void Duplicate(ILogger logger, Guid reservationId, string consumerName);
}
