using System.Text.Json;
using Orders.Application.Ports;
using Orders.Domain;

namespace Orders.Application.UseCases.GetOrderHistory;

public sealed record OrderHistoryEvent(long Id, string EventType, string Payload, DateTimeOffset OccurredAt);

/// <summary>
/// The temporal query result: Order state folded from the event stream up
/// to (and including) whatever "asOf" boundary was requested - "now" if
/// none was given. Null if no OrderCreated event exists yet (or existed
/// yet, as of that boundary).
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
/// Milestone 23: reconstructs Order state by folding over the append-only
/// event log rather than reading a current-state row - the event store is
/// the source of truth, and current-state projections are just one
/// read-optimized view of it. Temporal queries and the full audit trail
/// are free consequences of that, not separately-built features.
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
            }
        }

        return new OrderSnapshot(orderId, customerId, amount, currency, status, createdAt);
    }

    private sealed record CreatedPayload(string CustomerId, decimal Amount, string Currency);
}
