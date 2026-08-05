using System.Text.Json;
using BuildingBlocks;

namespace Payments.Service.Messaging;

public sealed class PaymentOutboxEventDispatcher(
    IPaymentEventPublisher paymentEventPublisher,
    IPaymentDecisionReplyPublisher decisionReplyPublisher) : IOutboxEventDispatcher
{
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);

    public Task<IReadOnlyDictionary<string, object?>> PublishAsync(OutboxMessage message, CancellationToken cancellationToken)
    {
        return message.EventType switch
        {
            nameof(PaymentDecided) => PublishPaymentDecidedAsync(message, cancellationToken),
            nameof(PaymentDecisionReplied) => PublishPaymentDecisionRepliedAsync(message, cancellationToken),
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

    private async Task<IReadOnlyDictionary<string, object?>> PublishPaymentDecisionRepliedAsync(OutboxMessage message, CancellationToken cancellationToken)
    {
        var reply = JsonSerializer.Deserialize<PaymentDecisionReplied>(message.Payload, SerializerOptions)
            ?? throw new JsonException("The outbox payload did not contain a PaymentDecisionReplied event.");

        await decisionReplyPublisher.PublishAsync(reply, cancellationToken);

        return new Dictionary<string, object?> { ["OrderId"] = reply.OrderId };
    }
}
