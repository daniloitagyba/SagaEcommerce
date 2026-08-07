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
/// an authorization Orders decided to charge, or voiding one it will never
/// charge.
///
/// Until now a payment was a single decision: approved or not, and the
/// money conceptually moved at that instant. That collapses the distinction
/// every card network actually makes - a hold placed at checkout, and funds
/// taken when the goods ship. Splitting it is what makes "authorized but
/// not yet charged" a state the system can be in, which in turn is what
/// makes an expiry sweeper meaningful (see PaymentAuthorizationSweeper) and
/// what lets a cancelled order release the shopper's money instead of
/// silently keeping it held.
///
/// Both operations are guarded inside the domain (Payment.TryCapture /
/// TrySettleWithoutCapture only act from Authorized), so a redelivered
/// command is a no-op rather than a double charge - the same reasoning as
/// the inbox, applied to a state transition instead of a message id.
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
            _ => SettlementOperation.Void
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
            // Nothing to settle. Not an error worth retrying or dead-lettering:
            // an order whose payment was never recorded (declined outright, or
            // an amount-only order that never reached Payments) has no hold to
            // release, and re-delivering this command will not create one.
            await transaction.RollbackAsync(cancellationToken);
            PaymentSettlementLog.NoPaymentToSettle(logger, orderId);
            return MessageProcessingResult.Processed;
        }

        var settledAt = DateTimeOffset.UtcNow;
        var changed = operation switch
        {
            SettlementOperation.Capture => payment.TryCapture(settledAt),
            // Milestone 70: guarded cumulatively inside the domain, so a
            // redelivered refund - or a second return of units already sent
            // back - cannot refund more than was ever charged.
            SettlementOperation.Refund => payment.TryRefund(refundAmount, settledAt),
            _ => payment.TrySettleWithoutCapture(PaymentStates.Voided, reason, settledAt)
        };

        if (!changed)
        {
            await transaction.RollbackAsync(cancellationToken);
            PaymentSettlementLog.AlreadySettled(logger, orderId, payment.State);
            return MessageProcessingResult.Duplicate;
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

                default:
                {
                    var request = JsonSerializer.Deserialize<PaymentVoidRequested>(payload, SerializerOptions)
                        ?? throw new JsonException("Empty void request.");
                    return (request.OrderId, request.CorrelationId, request.Reason, 0m);
                }
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
        Void,
        Refund
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
}
