using System.Text.Json;
using Orders.Application.Ports;
using Orders.Domain;

namespace Orders.Application.UseCases.GetOrderHistory;

public sealed record OrderHistoryEvent(long Id, string EventType, string Payload, DateTimeOffset OccurredAt);

/// <summary>
/// Represents an order state at a point in time.
/// </summary>
public sealed record OrderSnapshot(
    Guid OrderId,
    string? CustomerId,
    decimal? Amount,
    string? Currency,
    string Status,
    DateTimeOffset? CreatedAt);

public sealed record OrderHistoryResult(OrderSnapshot? Snapshot, IReadOnlyList<OrderHistoryEvent> Events);

/// <summary>
/// Reconstructs order history from events.
/// </summary>
public sealed class GetOrderHistoryHandler(IOrderEventStoreRepository repository)
{
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);

    public async Task<OrderHistoryResult> HandleAsync(Guid orderId, DateTimeOffset? asOf, CancellationToken cancellationToken)
    {
        var events = await repository.ListEventsAsync(orderId, asOf, cancellationToken);
        var snapshot = Fold(orderId, events);
        var historyEvents = events
            .Select(item => new OrderHistoryEvent(item.Id, item.EventType, item.Payload, item.OccurredAt))
            .ToList();

        return new OrderHistoryResult(snapshot, historyEvents);
    }

    private static OrderSnapshot? Fold(Guid orderId, IReadOnlyList<OrderEvent> events)
    {
        if (events.Count == 0)
        {
            return null;
        }

        string? customerId = null;
        decimal? amount = null;
        string? currency = null;
        DateTimeOffset? createdAt = null;
        var status = "Unknown";

        foreach (var domainEvent in events)
        {
            switch (domainEvent.EventType)
            {
                case "OrderCreated":
                    var created = JsonSerializer.Deserialize<CreatedPayload>(domainEvent.Payload, SerializerOptions);
                    customerId = created?.CustomerId;
                    amount = created?.Amount;
                    currency = created?.Currency;
                    createdAt = domainEvent.OccurredAt;
                    status = "Created";
                    break;
                case "OrderConfirmed":
                    status = "Confirmed";
                    break;
                case "OrderCancelled":
                    status = "Cancelled";
                    break;
                case "OrderBackordered":
                    status = "Backordered";
                    break;
                case "OrderPicking":
                    status = "Picking";
                    break;
                case "OrderShipped":
                    status = "Shipped";
                    break;
                case "OrderDelivered":
                    status = "Delivered";
                    break;
                case "OrderReturned":
                    status = "Returned";
                    break;
                case "OrderFulfillmentHold":
                    status = "FulfillmentHold";
                    break;
            }
        }

        return new OrderSnapshot(orderId, customerId, amount, currency, status, createdAt);
    }

    private sealed record CreatedPayload(string CustomerId, decimal Amount, string Currency);
}
