using System.Text.Json;
using BuildingBlocks;

namespace Inventory.Service.Messaging;

public sealed class InventoryOutboxEventDispatcher(IInventoryEventPublisher publisher) : IOutboxEventDispatcher
{
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);

    public async Task<IReadOnlyDictionary<string, object?>> PublishAsync(OutboxMessage message, CancellationToken cancellationToken)
    {
        var (reservationId, sku) = await DispatchAsync(message, cancellationToken);
        return new Dictionary<string, object?> { ["ReservationId"] = reservationId, ["Sku"] = sku };
    }

    private async Task<(Guid ReservationId, string Sku)> DispatchAsync(OutboxMessage message, CancellationToken cancellationToken)
    {
        switch (message.EventType)
        {
            case nameof(InventoryReservationReplied):
            {
                var reply = JsonSerializer.Deserialize<InventoryReservationReplied>(message.Payload, SerializerOptions)
                    ?? throw new JsonException("The outbox payload did not contain an InventoryReservationReplied event.");
                await publisher.PublishAsync(reply, cancellationToken);
                return (reply.ReservationId, reply.Sku);
            }

            case nameof(InventoryReservationCommitReplied):
            {
                var reply = JsonSerializer.Deserialize<InventoryReservationCommitReplied>(message.Payload, SerializerOptions)
                    ?? throw new JsonException("The outbox payload did not contain an InventoryReservationCommitReplied event.");
                await publisher.PublishAsync(reply, cancellationToken);
                return (reply.ReservationId, reply.Sku);
            }

            case nameof(InventoryReservationReleaseReplied):
            {
                var reply = JsonSerializer.Deserialize<InventoryReservationReleaseReplied>(message.Payload, SerializerOptions)
                    ?? throw new JsonException("The outbox payload did not contain an InventoryReservationReleaseReplied event.");
                await publisher.PublishAsync(reply, cancellationToken);
                return (reply.ReservationId, reply.Sku);
            }

            default:
                throw new JsonException($"Unsupported outbox event type '{message.EventType}'.");
        }
    }
}
