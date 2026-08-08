using System.Diagnostics;
using System.Text;
using System.Text.Json;
using BuildingBlocks;
using Confluent.Kafka;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Payments.Service.Data;

namespace Payments.Service;

public sealed class InvalidSettlementRequestException(string message, Exception? innerException = null)
    : Exception(message, innerException);

/// <summary>
/// Milestone 68: the second half of the two-phase payment flow - capturing
/// an authorization Orders decided to charge, or settling one it never
/// will (Milestone 81: cancellation, which may void a hold or refund a
/// capture depending on what it finds - see Payment.TryCancel) or partially
/// giving one back (Milestone 70: refund). Splits "approved" from "money
/// moved", the distinction every card network makes, which is what makes an
/// expiry sweeper meaningful (see PaymentAuthorizationSweeper). Every
/// operation is guarded inside the domain, so a redelivered command is a
/// no-op, not a double charge - the same reasoning as the inbox, applied to
/// a state transition.
/// </summary>
public sealed class PaymentSettlementProcessor(
    IServiceScopeFactory scopeFactory,
    IOptions<PaymentSettlementOptions> settlementOptions,
    ILogger<PaymentSettlementProcessor> logger)
{
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);
    private readonly PaymentSettlementOptions _options = settlementOptions.Value;

    public async Task<MessageProcessingResult> ProcessAsync(
        ConsumeResult<string, string> consumeResult,
        CancellationToken cancellationToken)
    {
        var operation = consumeResult.Topic switch
        {
            var topic when topic == _options.CaptureRequestedTopic => SettlementOperation.Capture,
            var topic when topic == _options.RefundRequestedTopic => SettlementOperation.Refund,
            var topic when topic == _options.CancellationRequestedTopic => SettlementOperation.Cancel,
            _ => throw new InvalidSettlementRequestException($"'{consumeResult.Topic}' is not a settlement topic this processor subscribes to.")
        };
        var (orderId, correlationId, reason, refundAmount) = Deserialize(consumeResult.Message.Value, operation);

        using var activity = OrdersTelemetry.StartActivity(
            $"payments.{operation.ToString().ToLowerInvariant()}",
            ActivityKind.Consumer,
            GetHeader(consumeResult.Message.Headers, MessagingHeaders.TraceParent),
            GetHeader(consumeResult.Message.Headers, MessagingHeaders.TraceState));
        activity?.SetTag("order.id", orderId);
        activity?.SetTag("correlation.id", correlationId);

        using var scope = logger.BeginScope(new Dictionary<string, object?>
        {
            ["CorrelationId"] = correlationId,
            ["OrderId"] = orderId
        });

        await using var serviceScope = scopeFactory.CreateAsyncScope();
        var dbContext = serviceScope.ServiceProvider.GetRequiredService<PaymentsDbContext>();

        await using var transaction = await dbContext.Database.BeginTransactionAsync(cancellationToken);

        var payment = await dbContext.Payments
            .OrderByDescending(item => item.DecidedAt)
            .FirstOrDefaultAsync(item => item.OrderId == orderId, cancellationToken);

        if (payment is null)
        {
            // Nothing to settle, and not worth retrying: no recorded payment means no hold to release.
            await transaction.RollbackAsync(cancellationToken);
            PaymentSettlementLog.NoPaymentToSettle(logger, orderId);
            return MessageProcessingResult.Processed;
        }

        var settledAt = DateTimeOffset.UtcNow;
        var changed = operation switch
        {
            SettlementOperation.Capture => payment.TryCapture(settledAt),
            // Milestone 70: guarded cumulatively inside the domain, so a redelivered refund can't exceed what was ever charged.
            SettlementOperation.Refund => payment.TryRefund(refundAmount, settledAt),
            // Milestone 81: decides void vs. refund from the payment's own
            // current state - see Payment.TryCancel. Replaces the old
            // method-agnostic "Void" operation entirely: nothing produces
            // that command any more, since a Pix payment is Captured the
            // instant it's approved and voiding it was never the right verb.
            SettlementOperation.Cancel => payment.TryCancel(reason, settledAt),
            _ => throw new UnreachableException($"Unhandled {nameof(SettlementOperation)} '{operation}'.")
        };

        if (!changed)
        {
            // Milestone 76: not every guard failure is a harmless redelivery.
            // A true duplicate is this exact operation landing twice, and the
            // payment is already sitting in the state it would have produced
            // - safe to drop silently, the first attempt's reply already
            // told the saga. Anything else is a genuine mismatch: most often
            // the expiry sweeper voiding/expiring the hold in the window
            // between the order shipping and the capture command arriving.
            // That capture can never happen now, and the saga has to be told
            // - the alternative is a shipped order nobody ever charged, with
            // nothing in the system recording that it went wrong.
            //
            // Milestone 81: Cancel is different in kind from the other
            // three - it has no single target state (it might void, might
            // refund, depending on what it finds), so "did it apply" can't
            // be compared against one expected outcome the way Capture and
            // Void can. Every state TryCancel refuses to move from -
            // Declined (nothing was ever approved), Expired (the hold
            // already lapsed), Voided or Refunded (already settled) - means
            // there is genuinely nothing left to do, not that a capture was
            // silently missed. Cancel-unchanged is therefore always the
            // benign case; unlike Capture, there is no "money should have
            // moved and didn't" reading of it that the saga needs to hear about.
            var isRedeliveryOfAlreadyAppliedOperation = operation switch
            {
                SettlementOperation.Capture => payment.State == PaymentStates.Captured,
                SettlementOperation.Refund => payment.State == PaymentStates.Refunded && payment.RefundedAmount >= payment.Amount,
                SettlementOperation.Cancel => true,
                _ => throw new UnreachableException($"Unhandled {nameof(SettlementOperation)} '{operation}'.")
            };

            if (isRedeliveryOfAlreadyAppliedOperation)
            {
                await transaction.RollbackAsync(cancellationToken);
                PaymentSettlementLog.AlreadySettled(logger, orderId, payment.State);
                return MessageProcessingResult.Duplicate;
            }

            var mismatchReply = new PaymentSettlementReplied(
                orderId,
                payment.Id,
                payment.State,
                payment.Amount,
                payment.Currency,
                correlationId,
                settledAt);

            dbContext.OutboxMessages.Add(OutboxMessage.Create(
                Guid.NewGuid(),
                nameof(PaymentSettlementReplied),
                JsonSerializer.Serialize(mismatchReply, SerializerOptions),
                settledAt,
                correlationId,
                Activity.Current?.Id,
                Activity.Current?.TraceStateString));

            await dbContext.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);

            activity?.SetTag("payment.state", payment.State);
            OrdersTelemetry.RecordProcessed("mismatch");
            PaymentSettlementLog.SettlementMismatch(logger, orderId, payment.Id, operation.ToString(), payment.State, correlationId);
            return MessageProcessingResult.Processed;
        }

        var reply = new PaymentSettlementReplied(
            orderId,
            payment.Id,
            payment.State,
            payment.Amount,
            payment.Currency,
            correlationId,
            settledAt);

        dbContext.OutboxMessages.Add(OutboxMessage.Create(
            Guid.NewGuid(),
            nameof(PaymentSettlementReplied),
            JsonSerializer.Serialize(reply, SerializerOptions),
            settledAt,
            correlationId,
            Activity.Current?.Id,
            Activity.Current?.TraceStateString));

        await dbContext.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        activity?.SetTag("payment.state", payment.State);
        OrdersTelemetry.RecordProcessed("success");
        PaymentSettlementLog.Settled(logger, orderId, payment.Id, payment.State, payment.Amount, correlationId);
        return MessageProcessingResult.Processed;
    }

    private static (Guid OrderId, string CorrelationId, string Reason, decimal RefundAmount) Deserialize(
        string payload,
        SettlementOperation operation)
    {
        try
        {
            switch (operation)
            {
                case SettlementOperation.Capture:
                {
                    var request = JsonSerializer.Deserialize<PaymentCaptureRequested>(payload, SerializerOptions)
                        ?? throw new JsonException("Empty capture request.");
                    return (request.OrderId, request.CorrelationId, "captured", 0m);
                }

                case SettlementOperation.Refund:
                {
                    var request = JsonSerializer.Deserialize<PaymentRefundRequested>(payload, SerializerOptions)
                        ?? throw new JsonException("Empty refund request.");
                    return (request.OrderId, request.CorrelationId, request.Reason, request.Amount);
                }

                case SettlementOperation.Cancel:
                {
                    var request = JsonSerializer.Deserialize<PaymentCancellationRequested>(payload, SerializerOptions)
                        ?? throw new JsonException("Empty cancellation request.");
                    return (request.OrderId, request.CorrelationId, request.Reason, 0m);
                }

                default:
                    throw new UnreachableException($"Unhandled {nameof(SettlementOperation)} '{operation}'.");
            }
        }
        catch (JsonException exception)
        {
            throw new InvalidSettlementRequestException("The Kafka message is not a valid settlement request.", exception);
        }
    }

    private enum SettlementOperation
    {
        Capture,
        Refund,
        Cancel
    }

    private static string? GetHeader(Headers headers, string key)
    {
        var header = headers.LastOrDefault(item => string.Equals(item.Key, key, StringComparison.Ordinal));
        return header is null ? null : Encoding.UTF8.GetString(header.GetValueBytes());
    }
}

public sealed partial class PaymentSettlementLog
{
    [LoggerMessage(EventId = 5100, Level = LogLevel.Information, Message = "Settled payment {PaymentId} for order {OrderId} as {State} ({Amount}) with correlation {CorrelationId}")]
    public static partial void Settled(ILogger logger, Guid orderId, Guid paymentId, string state, decimal amount, string correlationId);

    [LoggerMessage(EventId = 5101, Level = LogLevel.Information, Message = "Payment for order {OrderId} is already {State} - settlement request ignored")]
    public static partial void AlreadySettled(ILogger logger, Guid orderId, string state);

    [LoggerMessage(EventId = 5102, Level = LogLevel.Information, Message = "No payment recorded for order {OrderId} - nothing to settle")]
    public static partial void NoPaymentToSettle(ILogger logger, Guid orderId);

    [LoggerMessage(EventId = 5103, Level = LogLevel.Warning, Message = "Expired {Count} card authorization(s) that were never captured")]
    public static partial void ExpiredAuthorizations(ILogger logger, int count);

    [LoggerMessage(EventId = 5105, Level = LogLevel.Warning, Message = "Settlement mismatch for order {OrderId}: {Operation} could not apply because payment {PaymentId} is already {State} - reply published so the saga can react, not just this log line")]
    public static partial void SettlementMismatch(ILogger logger, Guid orderId, Guid paymentId, string operation, string state, string correlationId);
}
