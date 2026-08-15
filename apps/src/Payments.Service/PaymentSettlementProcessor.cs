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
/// The second half of the two-phase payment flow - capturing
/// an authorization Orders decided to charge, or settling one it never
/// will (cancellation, which may void a hold or refund a
/// capture depending on what it finds - see Payment.TryCancel) or partially
/// giving one back (refund). Splits "approved" from "money
/// moved", the distinction every card network makes, which is what makes an
/// expiry sweeper meaningful (see PaymentAuthorizationSweeper). Every
/// operation is guarded inside the domain, so a redelivered command is a
/// no-op, not a double charge - the same reasoning as the inbox, applied to
/// a state transition.
/// </summary>
public sealed class PaymentSettlementProcessor(
    IServiceScopeFactory scopeFactory,
    IOptions<PaymentSettlementOptions> settlementOptions,
    ILogger<PaymentSettlementProcessor> logger,
    TimeProvider? timeProvider = null)
{
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);
    private readonly PaymentSettlementOptions _options = settlementOptions.Value;
    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;

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
        var command = Deserialize(consumeResult.Message.Value, operation);
        var orderId = command.OrderId;
        var correlationId = command.CorrelationId;

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

        // Every state transition, including expiry, must serialize on the
        // payment row. Kafka preserves order only inside one topic/partition;
        // capture, refund and cancellation are different topics, and the
        // expiry sweeper is not a Kafka consumer at all.
        var payment = await dbContext.Payments
            .FromSqlInterpolated($"""
                SELECT *
                FROM payments
                WHERE is_primary AND order_id = {orderId}
                FOR UPDATE
                """)
            .SingleOrDefaultAsync(cancellationToken);

        if (payment is null)
        {
            // Nothing to settle, and not worth retrying: no recorded payment means no hold to release.
            await transaction.RollbackAsync(cancellationToken);
            PaymentSettlementLog.NoPaymentToSettle(logger, orderId);
            return MessageProcessingResult.Processed;
        }

        if (operation == SettlementOperation.Refund)
        {
            if (!string.Equals(payment.Currency, command.Currency, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidSettlementRequestException(
                    $"Refund currency '{command.Currency}' does not match payment currency '{payment.Currency}'.");
            }

            var inserted = await InboxStore.TryRecordWithinTransactionAsync(
                dbContext.Database, _options.ConsumerGroup, command.OperationId!.Value,
                consumeResult.Topic, consumeResult.Partition.Value, consumeResult.Offset.Value,
                correlationId, _timeProvider.GetUtcNow(), cancellationToken);

            if (inserted == 0)
            {
                await transaction.RollbackAsync(cancellationToken);
                OrdersTelemetry.RecordProcessed("duplicate");
                OrdersTelemetry.RecordInboxDuplicate(_options.ConsumerGroup);
                PaymentSettlementLog.DuplicateRefund(logger, command.OperationId.Value, orderId);
                return MessageProcessingResult.Duplicate;
            }
        }

        var settledAt = _timeProvider.GetUtcNow();
        var changed = operation switch
        {
            SettlementOperation.Capture => payment.TryCapture(settledAt),
            // ReturnId was claimed in the inbox above, in this same
            // transaction, so a redelivery cannot apply this delta twice.
            SettlementOperation.Refund => payment.TryRefund(command.RefundAmount, settledAt),
            // Decides void vs. refund from the payment's own
            // current state - see Payment.TryCancel. Replaces the old
            // method-agnostic "Void" operation entirely: nothing produces
            // that command any more, since a Pix payment is Captured the
            // instant it's approved and voiding it was never the right verb.
            SettlementOperation.Cancel => payment.TryCancel(command.Reason, settledAt),
            _ => throw new UnreachableException($"Unhandled {nameof(SettlementOperation)} '{operation}'.")
        };

        if (!changed)
        {
            // Not every guard failure is a harmless redelivery.
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
            // Cancel is different in kind from the other
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
                settledAt,
                RequiresReconciliation: true);

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
            settledAt,
            RequiresReconciliation: false);

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

    private static SettlementCommand Deserialize(
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
                        EnsureRequired(request.OrderId, request.CorrelationId);
                        return new SettlementCommand(
                            request.OrderId, request.CorrelationId, "captured", 0m, null, null);
                    }

                case SettlementOperation.Refund:
                    {
                        var request = JsonSerializer.Deserialize<PaymentRefundRequested>(payload, SerializerOptions)
                            ?? throw new JsonException("Empty refund request.");
                        EnsureRequired(request.OrderId, request.CorrelationId);
                        if (request.ReturnId == Guid.Empty || request.Amount <= 0m || string.IsNullOrWhiteSpace(request.Currency))
                        {
                            throw new InvalidSettlementRequestException(
                                "Refund requests require a return identifier, positive amount and currency.");
                        }

                        return new SettlementCommand(
                            request.OrderId,
                            request.CorrelationId,
                            request.Reason,
                            request.Amount,
                            request.ReturnId,
                            request.Currency);
                    }

                case SettlementOperation.Cancel:
                    {
                        var request = JsonSerializer.Deserialize<PaymentCancellationRequested>(payload, SerializerOptions)
                            ?? throw new JsonException("Empty cancellation request.");
                        EnsureRequired(request.OrderId, request.CorrelationId);
                        return new SettlementCommand(
                            request.OrderId, request.CorrelationId, request.Reason, 0m, null, null);
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

    private static void EnsureRequired(Guid orderId, string correlationId)
    {
        if (orderId == Guid.Empty || string.IsNullOrWhiteSpace(correlationId))
        {
            throw new InvalidSettlementRequestException(
                "Settlement requests require an order identifier and correlation identifier.");
        }
    }

    private sealed record SettlementCommand(
        Guid OrderId,
        string CorrelationId,
        string Reason,
        decimal RefundAmount,
        Guid? OperationId,
        string? Currency);

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

    [LoggerMessage(EventId = 5106, Level = LogLevel.Information, Message = "Skipped duplicate refund for return {ReturnId} and order {OrderId}")]
    public static partial void DuplicateRefund(ILogger logger, Guid returnId, Guid orderId);
}
