using System.Text.Json;
using BuildingBlocks;

namespace Orders.Infrastructure.Messaging;

public sealed class OrderOutboxEventDispatcher(
    IOrderEventPublisher publisher,
    IPaymentSettlementCommandPublisher settlementPublisher) : IOutboxEventDispatcher
{
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);

    public Task<IReadOnlyDictionary<string, object?>> PublishAsync(OutboxMessage message, CancellationToken cancellationToken)
    {
        return message.EventType switch
        {
            nameof(OrderCreated) => PublishOrderCreatedAsync(message, cancellationToken),
            // The fulfilment API queues these in the same
            // transaction as the status change, so they ride the outbox
            // rather than being produced inline - a capture command must
            // not survive a rolled-back "Shipped".
            nameof(PaymentCaptureRequested) => PublishCaptureAsync(message, cancellationToken),
            // Replaces PaymentVoidRequested - a cancellation
            // may need to void a hold or refund a capture, decided by
            // Payments from the payment's own state.
            nameof(PaymentCancellationRequested) => PublishCancellationAsync(message, cancellationToken),
            // A return queues both, in the same transaction
            // as the return itself. A post-commit
            // cancellation queues a restock the same way.
            nameof(PaymentRefundRequested) => PublishRefundAsync(message, cancellationToken),
            nameof(InventoryRestockRequested) => PublishRestockAsync(message, cancellationToken),
            // A cancellation from Backordered queues this instead of a restock - nothing was ever committed to give back.
            nameof(BackorderCancellationRequested) => PublishBackorderCancellationAsync(message, cancellationToken),
            _ => throw new JsonException($"Unsupported outbox event type '{message.EventType}'.")
        };
    }

    private async Task<IReadOnlyDictionary<string, object?>> PublishOrderCreatedAsync(OutboxMessage message, CancellationToken cancellationToken)
    {
        var orderCreated = JsonSerializer.Deserialize<OrderCreated>(message.Payload, SerializerOptions)
            ?? throw new JsonException("The outbox payload did not contain an OrderCreated event.");

        await publisher.PublishAsync(orderCreated, cancellationToken);

        return new Dictionary<string, object?> { ["OrderId"] = orderCreated.OrderId };
    }

    private async Task<IReadOnlyDictionary<string, object?>> PublishCaptureAsync(OutboxMessage message, CancellationToken cancellationToken)
    {
        var request = JsonSerializer.Deserialize<PaymentCaptureRequested>(message.Payload, SerializerOptions)
            ?? throw new JsonException("The outbox payload did not contain a PaymentCaptureRequested command.");

        await settlementPublisher.PublishCaptureAsync(request, cancellationToken);

        return new Dictionary<string, object?> { ["OrderId"] = request.OrderId };
    }

    private async Task<IReadOnlyDictionary<string, object?>> PublishRefundAsync(OutboxMessage message, CancellationToken cancellationToken)
    {
        var request = JsonSerializer.Deserialize<PaymentRefundRequested>(message.Payload, SerializerOptions)
            ?? throw new JsonException("The outbox payload did not contain a PaymentRefundRequested command.");

        await settlementPublisher.PublishRefundAsync(request, cancellationToken);

        return new Dictionary<string, object?> { ["OrderId"] = request.OrderId, ["RefundAmount"] = request.Amount };
    }

    private async Task<IReadOnlyDictionary<string, object?>> PublishRestockAsync(OutboxMessage message, CancellationToken cancellationToken)
    {
        var request = JsonSerializer.Deserialize<InventoryRestockRequested>(message.Payload, SerializerOptions)
            ?? throw new JsonException("The outbox payload did not contain an InventoryRestockRequested command.");

        await settlementPublisher.PublishRestockAsync(request, cancellationToken);

        return new Dictionary<string, object?> { ["OrderId"] = request.OrderId, ["Sku"] = request.Sku };
    }

    private async Task<IReadOnlyDictionary<string, object?>> PublishCancellationAsync(OutboxMessage message, CancellationToken cancellationToken)
    {
        var request = JsonSerializer.Deserialize<PaymentCancellationRequested>(message.Payload, SerializerOptions)
            ?? throw new JsonException("The outbox payload did not contain a PaymentCancellationRequested command.");

        await settlementPublisher.PublishCancellationAsync(request, cancellationToken);

        return new Dictionary<string, object?> { ["OrderId"] = request.OrderId };
    }

    private async Task<IReadOnlyDictionary<string, object?>> PublishBackorderCancellationAsync(OutboxMessage message, CancellationToken cancellationToken)
    {
        var request = JsonSerializer.Deserialize<BackorderCancellationRequested>(message.Payload, SerializerOptions)
            ?? throw new JsonException("The outbox payload did not contain a BackorderCancellationRequested command.");

        await settlementPublisher.PublishBackorderCancellationAsync(request, cancellationToken);

        return new Dictionary<string, object?> { ["OrderId"] = request.OrderId };
    }
}
