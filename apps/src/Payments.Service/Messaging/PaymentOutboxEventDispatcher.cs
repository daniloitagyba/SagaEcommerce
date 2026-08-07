using System.Text.Json;
using BuildingBlocks;

namespace Payments.Service.Messaging;

public sealed class PaymentOutboxEventDispatcher(
    IPaymentEventPublisher paymentEventPublisher,
    IPaymentDecisionReplyPublisher decisionReplyPublisher,
    IPaymentSettlementPublisher settlementPublisher) : IOutboxEventDispatcher
{
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);

    public Task<IReadOnlyDictionary<string, object?>> PublishAsync(OutboxMessage message, CancellationToken cancellationToken)
    {
        return message.EventType switch
        {
            nameof(PaymentDecided) => PublishPaymentDecidedAsync(message, cancellationToken),
            nameof(PaymentDecisionReplied) => PublishPaymentDecisionRepliedAsync(message, cancellationToken),
            nameof(PaymentSettlementReplied) => PublishPaymentSettlementRepliedAsync(message, cancellationToken),
            _ => throw new JsonException($"Unsupported outbox event type '{message.EventType}'.")
        };
    }

    private async Task<IReadOnlyDictionary<string, object?>> PublishPaymentDecidedAsync(OutboxMessage message, CancellationToken cancellationToken)
    {
        var paymentDecided = JsonSerializer.Deserialize<PaymentDecided>(message.Payload, SerializerOptions)
            ?? throw new JsonException("The outbox payload did not contain a PaymentDecided event.");

        await paymentEventPublisher.PublishAsync(paymentDecided, cancellationToken);

        return new Dictionary<string, object?> { ["OrderId"] = paymentDecided.OrderId };
    }

    private async Task<IReadOnlyDictionary<string, object?>> PublishPaymentSettlementRepliedAsync(OutboxMessage message, CancellationToken cancellationToken)
    {
        var reply = JsonSerializer.Deserialize<PaymentSettlementReplied>(message.Payload, SerializerOptions)
            ?? throw new JsonException("The outbox payload did not contain a PaymentSettlementReplied event.");

        await settlementPublisher.PublishAsync(reply, cancellationToken);

        return new Dictionary<string, object?> { ["OrderId"] = reply.OrderId, ["PaymentState"] = reply.State };
    }

    private async Task<IReadOnlyDictionary<string, object?>> PublishPaymentDecisionRepliedAsync(OutboxMessage message, CancellationToken cancellationToken)
    {
        var reply = JsonSerializer.Deserialize<PaymentDecisionReplied>(message.Payload, SerializerOptions)
            ?? throw new JsonException("The outbox payload did not contain a PaymentDecisionReplied event.");

        await decisionReplyPublisher.PublishAsync(reply, cancellationToken);

        return new Dictionary<string, object?> { ["OrderId"] = reply.OrderId };
    }
}
