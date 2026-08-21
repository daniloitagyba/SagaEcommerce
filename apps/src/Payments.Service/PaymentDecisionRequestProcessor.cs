using System.Diagnostics;
using System.Text;
using System.Text.Json;
using BuildingBlocks;
using Confluent.Kafka;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Payments.Service.Data;
using Polly;
using Polly.Registry;

namespace Payments.Service;

public sealed class InvalidPaymentDecisionRequestException(string message, Exception? innerException = null)
    : Exception(message, innerException);

/// <summary>The orchestrated flow's counterpart to PaymentMessageProcessor: inbox dedup, a persisted Payment row, an outbox-published reply.</summary>
public sealed class PaymentDecisionRequestProcessor(
    IServiceScopeFactory scopeFactory,
    IOptions<PaymentDecisionRequestOptions> requestOptions,
    ILogger<PaymentDecisionRequestProcessor> logger,
    ResiliencePipelineProvider<string> pipelineProvider,
    TimeProvider? timeProvider = null)
{
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);
    private readonly PaymentDecisionRequestOptions _requestOptions = requestOptions.Value;
    private readonly ResiliencePipeline _pipeline = pipelineProvider.GetPipeline(ResilienceExtensions.PostgresTransactionPipeline);
    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;

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

        return await _pipeline.ExecuteAsync(async ct =>
        {
            await using var transaction = await dbContext.Database.BeginTransactionAsync(ct);

            var processedAt = _timeProvider.GetUtcNow();
            var insertedRows = await InboxStore.TryRecordWithinTransactionAsync(
                dbContext.Database, _requestOptions.ConsumerGroup, request.OrderId,
                consumeResult.Topic, consumeResult.Partition.Value, consumeResult.Offset.Value,
                correlationId, processedAt, ct);

            if (insertedRows == 0)
            {
                await transaction.RollbackAsync(ct);
                OrdersTelemetry.RecordProcessed("duplicate");
                OrdersTelemetry.RecordInboxDuplicate(_requestOptions.ConsumerGroup);
                PaymentsLog.Duplicate(logger, request.OrderId, _requestOptions.ConsumerGroup);
                return MessageProcessingResult.Duplicate;
            }

            var decisionCoordinator = serviceScope.ServiceProvider.GetRequiredService<PaymentDecisionCoordinator>();
            var decision = await decisionCoordinator.GetOrCreateAsync(
                new PaymentDecisionInput(
                    request.OrderId,
                    request.CustomerId,
                    request.Amount,
                    request.Currency,
                    request.PaymentMethod,
                    request.ShippingPostalPrefix,
                    correlationId),
                processedAt,
                ct);
            var payment = decision.Payment;
            var approved = payment.Approved;
            var reply = new PaymentDecisionReplied(request.OrderId, approved, correlationId, processedAt);
            var outboxMessage = OutboxMessage.Create(
                payment.Id,
                nameof(PaymentDecisionReplied),
                JsonSerializer.Serialize(reply, SerializerOptions),
                processedAt,
                correlationId,
                Activity.Current?.Id,
                Activity.Current?.TraceStateString);

            dbContext.OutboxMessages.Add(outboxMessage);
            await dbContext.SaveChangesAsync(ct);
            await transaction.CommitAsync(ct);

            activity?.SetTag("payment.approved", approved);
            activity?.SetTag("payment.decision_created", decision.Created);
            activity?.SetTag("payment.risk_score", decision.Assessment?.Score);
            OrdersTelemetry.RecordProcessed("success");
            OrdersTelemetry.RecordPaymentDecided(approved);
            if (decision.Assessment is { } assessment)
            {
                PaymentsLog.DecidedWithRisk(
                    logger, request.OrderId, payment.Id, approved,
                    assessment.Score, assessment.ReasonSummary, correlationId);
            }
            else
            {
                PaymentsLog.ReusedDecision(logger, request.OrderId, payment.Id, correlationId);
            }
            return MessageProcessingResult.Processed;
        }, cancellationToken);
    }

    /// <summary>Not private: Payments.UnitTests exercises this directly - the request's shape validation has no database dependency at all.</summary>
    internal static PaymentDecisionRequested DeserializeAndValidate(string payload)
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
