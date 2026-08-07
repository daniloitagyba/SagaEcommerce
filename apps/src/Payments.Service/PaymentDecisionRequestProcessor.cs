using System.Diagnostics;
using System.Text;
using System.Text.Json;
using BuildingBlocks;
using Confluent.Kafka;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Payments.Service.Data;
using Payments.Service.Domain;
using Payments.Service.Risk;

namespace Payments.Service;

public sealed class InvalidPaymentDecisionRequestException(string message, Exception? innerException = null)
    : Exception(message, innerException);

/// <summary>
/// Milestone 65: the orchestrated flow's counterpart to
/// PaymentMessageProcessor, brought to the same reliability bar - inbox
/// dedup, a persisted Payment row, an outbox-published reply. The only
/// genuine difference from choreography left is what it reacts to (an
/// explicit request vs. autonomously deciding on OrderCreated), which is
/// what makes the comparison legitimate. PaymentDecisionRequested has no
/// EventId of its own, so OrderId serves as the inbox dedup key instead,
/// valid since only one decision request is ever outstanding per order.
/// </summary>
public sealed class PaymentDecisionRequestProcessor(
    IServiceScopeFactory scopeFactory,
    IOptions<PaymentDecisionRequestOptions> requestOptions,
    IOptions<PaymentRiskOptions> riskOptions,
    ILogger<PaymentDecisionRequestProcessor> logger)
{
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);
    private readonly PaymentRiskOptions _riskOptions = riskOptions.Value;
    private readonly PaymentDecisionRequestOptions _requestOptions = requestOptions.Value;

    public async Task<MessageProcessingResult> ProcessAsync(
        ConsumeResult<string, string> consumeResult,
        CancellationToken cancellationToken)
    {
        var request = DeserializeAndValidate(consumeResult.Message.Value);
        var correlationId = GetHeader(consumeResult.Message.Headers, MessagingHeaders.CorrelationId)
            ?? request.CorrelationId;
        var traceParent = GetHeader(consumeResult.Message.Headers, MessagingHeaders.TraceParent);
        var traceState = GetHeader(consumeResult.Message.Headers, MessagingHeaders.TraceState);

        using var activity = OrdersTelemetry.StartActivity(
            "payments.decision_request.process",
            ActivityKind.Consumer,
            traceParent,
            traceState);
        activity?.SetTag("messaging.system", "kafka");
        activity?.SetTag("messaging.destination.name", consumeResult.Topic);
        activity?.SetTag("messaging.operation.type", "process");
        activity?.SetTag("order.id", request.OrderId);
        activity?.SetTag("correlation.id", correlationId);

        using var scope = logger.BeginScope(new Dictionary<string, object?>
        {
            ["CorrelationId"] = correlationId,
            ["OrderId"] = request.OrderId,
            ["TraceId"] = activity?.TraceId.ToString() ?? string.Empty
        });

        await using var serviceScope = scopeFactory.CreateAsyncScope();
        var dbContext = serviceScope.ServiceProvider.GetRequiredService<PaymentsDbContext>();

        await using var transaction = await dbContext.Database.BeginTransactionAsync(cancellationToken);

        var processedAt = DateTimeOffset.UtcNow;
        var insertedRows = await dbContext.Database.ExecuteSqlInterpolatedAsync(
            $"""
            INSERT INTO inbox_messages (consumer_name, event_id, topic, partition, "offset", correlation_id, processed_at)
            VALUES ({_requestOptions.ConsumerGroup}, {request.OrderId}, {consumeResult.Topic}, {consumeResult.Partition.Value}, {consumeResult.Offset.Value}, {correlationId}, {processedAt})
            ON CONFLICT (consumer_name, event_id) DO NOTHING
            """,
            cancellationToken);

        if (insertedRows == 0)
        {
            await transaction.RollbackAsync(cancellationToken);
            OrdersTelemetry.RecordProcessed("duplicate");
            OrdersTelemetry.RecordInboxDuplicate(_requestOptions.ConsumerGroup);
            PaymentsLog.Duplicate(logger, request.OrderId, _requestOptions.ConsumerGroup);
            return MessageProcessingResult.Duplicate;
        }

        // Milestone 66: the same scored risk rules the choreographed path runs, keeping the two saga paths genuinely comparable.
        var riskEvaluator = serviceScope.ServiceProvider.GetRequiredService<PaymentRiskEvaluator>();
        var assessment = await riskEvaluator.EvaluateAsync(
            request.CustomerId,
            request.Amount,
            request.ShippingPostalPrefix,
            processedAt,
            cancellationToken);
        var approved = assessment.Approved;

        var payment = Payment.Authorize(
            request.OrderId,
            request.CustomerId,
            request.Amount,
            request.Currency,
            request.PaymentMethod,
            request.ShippingPostalPrefix,
            approved,
            processedAt,
            _riskOptions.SettlementWindowFor(request.PaymentMethod),
            correlationId);
        var reply = new PaymentDecisionReplied(request.OrderId, approved, correlationId, processedAt);
        var outboxMessage = OutboxMessage.Create(
            payment.Id,
            nameof(PaymentDecisionReplied),
            JsonSerializer.Serialize(reply, SerializerOptions),
            processedAt,
            correlationId,
            Activity.Current?.Id,
            Activity.Current?.TraceStateString);

        dbContext.Payments.Add(payment);
        dbContext.OutboxMessages.Add(outboxMessage);
        await dbContext.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        activity?.SetTag("payment.approved", approved);
        activity?.SetTag("payment.risk_score", assessment.Score);
        OrdersTelemetry.RecordProcessed("success");
        OrdersTelemetry.RecordPaymentDecided(approved);
        PaymentsLog.DecidedWithRisk(logger, request.OrderId, payment.Id, approved, assessment.Score, assessment.ReasonSummary, correlationId);
        return MessageProcessingResult.Processed;
    }

    private static PaymentDecisionRequested DeserializeAndValidate(string payload)
    {
        PaymentDecisionRequested request;
        try
        {
            request = JsonSerializer.Deserialize<PaymentDecisionRequested>(payload, SerializerOptions)
                ?? throw new JsonException("The Kafka message did not contain a PaymentDecisionRequested event.");
        }
        catch (JsonException exception)
        {
            throw new InvalidPaymentDecisionRequestException("The Kafka message is not a valid PaymentDecisionRequested event.", exception);
        }

        if (request.OrderId == Guid.Empty)
        {
            throw new InvalidPaymentDecisionRequestException("The PaymentDecisionRequested event's order identifier is required.");
        }

        return request;
    }

    private static string? GetHeader(Headers headers, string key)
    {
        var header = headers.LastOrDefault(item => string.Equals(item.Key, key, StringComparison.Ordinal));
        return header is null ? null : Encoding.UTF8.GetString(header.GetValueBytes());
    }
}
