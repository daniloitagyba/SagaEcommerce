using BuildingBlocks;
using StackExchange.Redis;

namespace Catalog.Service.Data;

public sealed record BestsellerEntry(string Sku, long UnitsSold);

/// <summary>
/// The read side of the bestsellers projection - Orders.Worker
/// (RedisBestsellersStore) is the only writer, on every confirmed saga.
/// Ranking data lives entirely in Redis; this only reads it, then asks
/// MongoDB (ProductRepository) for the product details to display next to
/// the rank. Two stores, two jobs: Redis for "who's winning right now",
/// MongoDB for "what is this product".
/// </summary>
public sealed class BestsellersReader(IConnectionMultiplexer connectionMultiplexer)
{
    public async Task<IReadOnlyList<BestsellerEntry>> GetTopAsync(string? categorySlug, int limit, CancellationToken cancellationToken)
    {
        var database = connectionMultiplexer.GetDatabase();
        var key = string.IsNullOrWhiteSpace(categorySlug) ? BestsellersKeys.Global : BestsellersKeys.Category(categorySlug);

        var entries = await database.SortedSetRangeByScoreWithScoresAsync(
            key,
            order: Order.Descending,
            take: limit).WaitAsync(cancellationToken);

        return entries
            .Select(entry => new BestsellerEntry((string)entry.Element!, (long)entry.Score))
            .ToList();
    }
}
