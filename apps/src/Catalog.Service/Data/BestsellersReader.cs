using BuildingBlocks;
using StackExchange.Redis;

namespace Catalog.Service.Data;

public sealed record BestsellerEntry(string Sku, long UnitsSold);

/// <summary>Reads the bestsellers projection ranking from Redis; product details are read separately from MongoDB.</summary>
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
