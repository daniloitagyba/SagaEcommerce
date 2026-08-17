using Orders.Application.Ports;
using Orders.Application.UseCases.ListOrderSummaries;

namespace Orders.UnitTests;

/// <summary>Covers ClampLimit and the handler's delegation to IOrderSummaryRepository via a spy fake, since OrderSummary is EF-only to construct meaningfully.</summary>
public sealed class ListOrderSummariesHandlerTests
{
    [Theory]
    [InlineData(null, 20)]
    [InlineData(1, 1)]
    [InlineData(100, 100)]
    [InlineData(150, 100)]
    [InlineData(0, 1)]
    [InlineData(-5, 1)]
    public void ClampLimitStaysWithinOneAndOneHundred(int? requested, int expected)
    {
        Assert.Equal(expected, ListOrderSummariesHandler.ClampLimit(requested));
    }

    [Fact]
    public async Task AnUnclampedLimitReachesTheRepositoryAlreadyClamped()
    {
        var repository = new SpyOrderSummaryRepository();
        var handler = new ListOrderSummariesHandler(repository);

        await handler.HandleAsync(status: null, customerId: "customer-1", cursor: null, limit: 500, CancellationToken.None);

        Assert.Equal(100, repository.LastLimit);
    }

    [Fact]
    public async Task StatusAndCustomerIdAndCursorAllPassThroughUnchanged()
    {
        var repository = new SpyOrderSummaryRepository();
        var handler = new ListOrderSummariesHandler(repository);
        var cursor = new OrderSummaryCursor(DateTimeOffset.UtcNow, Guid.NewGuid());

        await handler.HandleAsync(status: "Confirmed", customerId: "customer-2", cursor: cursor, limit: 10, CancellationToken.None);

        Assert.Equal("Confirmed", repository.LastStatus);
        Assert.Equal("customer-2", repository.LastCustomerId);
        Assert.Same(cursor, repository.LastCursor);
        Assert.Equal(10, repository.LastLimit);
    }

    /// <summary>Admin cross-customer listing: null customerId must reach the repository as null, not get coerced into an empty-string filter that would match nothing.</summary>
    [Fact]
    public async Task ANullCustomerIdReachesTheRepositoryAsNull()
    {
        var repository = new SpyOrderSummaryRepository();
        var handler = new ListOrderSummariesHandler(repository);

        await handler.HandleAsync(status: null, customerId: null, cursor: null, limit: 20, CancellationToken.None);

        Assert.Null(repository.LastCustomerId);
    }

    private sealed class SpyOrderSummaryRepository : IOrderSummaryRepository
    {
        public string? LastStatus { get; private set; }
        public string? LastCustomerId { get; private set; }
        public OrderSummaryCursor? LastCursor { get; private set; }
        public int LastLimit { get; private set; }

        public Task<IReadOnlyList<Orders.Domain.OrderSummary>> ListAsync(
            string? status, string? customerId, OrderSummaryCursor? cursor, int limit, CancellationToken cancellationToken)
        {
            LastStatus = status;
            LastCustomerId = customerId;
            LastCursor = cursor;
            LastLimit = limit;
            return Task.FromResult<IReadOnlyList<Orders.Domain.OrderSummary>>([]);
        }
    }
}
