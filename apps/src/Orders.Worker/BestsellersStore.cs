using BuildingBlocks;
using StackExchange.Redis;

namespace Orders.Worker;

public interface IBestsellersStore
{
    Task RecordSaleAsync(string sku, string? categorySlug, int quantity, CancellationToken cancellationToken);
}

/// <summary>
/// Tracks best-selling products.
/// </summary>
public sealed class RedisBestsellersStore(IConnectionMultiplexer connectionMultiplexer) : IBestsellersStore
{
    public async Task RecordSaleAsync(string sku, string? categorySlug, int quantity, CancellationToken cancellationToken)
    {
        var database = connectionMultiplexer.GetDatabase();
        await database.SortedSetIncrementAsync(BestsellersKeys.Global, sku, quantity).WaitAsync(cancellationToken);

        if (!string.IsNullOrWhiteSpace(categorySlug))
        {
            await database.SortedSetIncrementAsync(BestsellersKeys.Category(categorySlug), sku, quantity).WaitAsync(cancellationToken);
        }
    }
}
