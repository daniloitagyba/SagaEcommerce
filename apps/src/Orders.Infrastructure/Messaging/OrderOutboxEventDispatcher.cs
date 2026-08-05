using System.Text.Json;
using BuildingBlocks;

namespace Orders.Infrastructure.Messaging;

public sealed class OrderOutboxEventDispatcher(IOrderEventPublisher publisher) : IOutboxEventDispatcher
{
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);

    public async Task<IReadOnlyDictionary<string, object?>> PublishAsync(OutboxMessage message, CancellationToken cancellationToken)
    {
        if (!string.Equals(message.EventType, nameof(OrderCreated), StringComparison.Ordinal))
        {
            throw new JsonException($"Unsupported outbox event type '{message.EventType}'.");
        }

        var orderCreated = JsonSerializer.Deserialize<OrderCreated>(message.Payload, SerializerOptions)
            ?? throw new JsonException("The outbox payload did not contain an OrderCreated event.");

        await publisher.PublishAsync(orderCreated, cancellationToken);

        return new Dictionary<string, object?> { ["OrderId"] = orderCreated.OrderId };
    }
}
