using System.Text.Json;
using Orders.Application.Ports;
using Orders.Application.UseCases.GetOrderHistory;
using Orders.Domain;

namespace Orders.UnitTests;

/// <summary>The temporal fold (Order state reconstructed from the event stream, not a current-state row); a hand-rolled IOrderEventStoreRepository fake since the fold logic has no database dependency.</summary>
public sealed class GetOrderHistoryHandlerTests
{
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);

    [Fact]
    public async Task NoEventsAtAllYieldsANullSnapshotButAnEmptyEventList()
    {
        var handler = new GetOrderHistoryHandler(new FakeEventStoreRepository([]));

        var result = await handler.HandleAsync(Guid.NewGuid(), asOf: null, CancellationToken.None);

        Assert.Null(result.Snapshot);
        Assert.Empty(result.Events);
    }

    [Fact]
    public async Task AnOrderCreatedEventPopulatesTheSnapshotFromItsPayload()
    {
        var orderId = Guid.NewGuid();
        var createdAt = DateTimeOffset.UtcNow.AddMinutes(-10);
        var events = new List<OrderEvent>
        {
            CreatedEvent(1, orderId, "customer-1", 49.90m, "BRL", createdAt)
        };
        var handler = new GetOrderHistoryHandler(new FakeEventStoreRepository(events));

        var result = await handler.HandleAsync(orderId, asOf: null, CancellationToken.None);

        Assert.NotNull(result.Snapshot);
        Assert.Equal(orderId, result.Snapshot!.OrderId);
        Assert.Equal("customer-1", result.Snapshot.CustomerId);
        Assert.Equal(49.90m, result.Snapshot.Amount);
        Assert.Equal("BRL", result.Snapshot.Currency);
        Assert.Equal("Created", result.Snapshot.Status);
        Assert.Equal(createdAt, result.Snapshot.CreatedAt);
        Assert.Single(result.Events);
    }

    [Fact]
    public async Task LaterEventsAdvanceTheStatusWithoutLosingTheOriginalCreationDetails()
    {
        var orderId = Guid.NewGuid();
        var createdAt = DateTimeOffset.UtcNow.AddHours(-1);
        var events = new List<OrderEvent>
        {
            CreatedEvent(1, orderId, "customer-2", 120m, "BRL", createdAt),
            OrderEvent.ForTesting(2, orderId, "OrderConfirmed", "{}", createdAt.AddMinutes(5))
        };
        var handler = new GetOrderHistoryHandler(new FakeEventStoreRepository(events));

        var result = await handler.HandleAsync(orderId, asOf: null, CancellationToken.None);

        Assert.Equal("Confirmed", result.Snapshot!.Status);
        Assert.Equal("customer-2", result.Snapshot.CustomerId);
        Assert.Equal(120m, result.Snapshot.Amount);
        Assert.Equal(createdAt, result.Snapshot.CreatedAt);
        Assert.Equal(2, result.Events.Count);
    }

    [Fact]
    public async Task ACancellationAfterConfirmationEndsWithCancelledNotConfirmed()
    {
        var orderId = Guid.NewGuid();
        var createdAt = DateTimeOffset.UtcNow.AddHours(-2);
        var events = new List<OrderEvent>
        {
            CreatedEvent(1, orderId, "customer-3", 75m, "BRL", createdAt),
            OrderEvent.ForTesting(2, orderId, "OrderConfirmed", "{}", createdAt.AddMinutes(5)),
            OrderEvent.ForTesting(3, orderId, "OrderCancelled", "{}", createdAt.AddMinutes(10))
        };
        var handler = new GetOrderHistoryHandler(new FakeEventStoreRepository(events));

        var result = await handler.HandleAsync(orderId, asOf: null, CancellationToken.None);

        Assert.Equal("Cancelled", result.Snapshot!.Status);
    }

    [Fact]
    public async Task AsOfLimitsTheFoldToEventsAtOrBeforeTheBoundary()
    {
        var orderId = Guid.NewGuid();
        var createdAt = DateTimeOffset.UtcNow.AddHours(-3);
        var confirmedAt = createdAt.AddMinutes(5);
        var allEvents = new List<OrderEvent>
        {
            CreatedEvent(1, orderId, "customer-4", 60m, "BRL", createdAt),
            OrderEvent.ForTesting(2, orderId, "OrderConfirmed", "{}", confirmedAt)
        };
        var handler = new GetOrderHistoryHandler(new FakeEventStoreRepository(allEvents, asOfAware: true));

        var result = await handler.HandleAsync(orderId, asOf: createdAt, CancellationToken.None);

        Assert.Equal("Created", result.Snapshot!.Status);
        Assert.Single(result.Events);
    }

    private static OrderEvent CreatedEvent(long id, Guid orderId, string customerId, decimal amount, string currency, DateTimeOffset occurredAt)
    {
        var payload = JsonSerializer.Serialize(new { CustomerId = customerId, Amount = amount, Currency = currency }, SerializerOptions);
        return OrderEvent.ForTesting(id, orderId, "OrderCreated", payload, occurredAt);
    }

    private sealed class FakeEventStoreRepository(IReadOnlyList<OrderEvent> events, bool asOfAware = false) : IOrderEventStoreRepository
    {
        public Task<IReadOnlyList<OrderEvent>> ListEventsAsync(Guid orderId, DateTimeOffset? asOf, CancellationToken cancellationToken)
        {
            var matching = events.Where(item => item.OrderId == orderId);
            if (asOfAware && asOf is { } boundary)
            {
                matching = matching.Where(item => item.OccurredAt <= boundary);
            }

            return Task.FromResult<IReadOnlyList<OrderEvent>>(matching.OrderBy(item => item.Id).ToList());
        }
    }
}
