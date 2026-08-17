using System.Text.Json;
using Cart.Service.Domain;
using StackExchange.Redis;

namespace Cart.Service.Data;

/// <summary>Maps the domain CRDT to its Redis JSON representation.</summary>
internal static class CartCrdtCodec
{
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);

    public static CartCrdtState Read(IReadOnlyList<HashEntry> entries, Func<string, bool> isMetadataField)
    {
        var persisted = entries.FirstOrDefault(entry => entry.Name == "__crdt").Value;
        if (persisted.HasValue)
        {
            var stored = JsonSerializer.Deserialize<PersistedCartCrdtState>((string)persisted!, SerializerOptions)
                ?? throw new InvalidOperationException("The persisted cart CRDT state deserialized to null.");
            return new CartCrdtState(
                stored.Items.ToDictionary(
                    entry => entry.Key,
                    entry => new CartItemCrdt(
                        entry.Value.LiveDots,
                        entry.Value.TombstoneDots,
                        entry.Value.Counters.ToDictionary(
                            counter => counter.Key,
                            counter => (counter.Value.Positive, counter.Value.Negative),
                            StringComparer.Ordinal)),
                    StringComparer.Ordinal),
                new Dictionary<string, CartItemMetadata>(stored.Metadata, StringComparer.Ordinal));
        }

        var items = entries
            .Where(entry => !isMetadataField(entry.Name.ToString()))
            .Select(entry => JsonSerializer.Deserialize<CartLineItem>((string)entry.Value!, SerializerOptions)
                ?? throw new InvalidOperationException("A cart line item value deserialized to null."))
            .ToList();
        return FromLegacyLines(items);
    }

    public static string Serialize(CartCrdtState state) =>
        JsonSerializer.Serialize(
            new PersistedCartCrdtState(
                state.Items.ToDictionary(
                    entry => entry.Key,
                    entry => new PersistedCartItemCrdt(
                        new HashSet<CartDot>(entry.Value.LiveDots),
                        new HashSet<CartDot>(entry.Value.TombstoneDots),
                        entry.Value.Counters.ToDictionary(
                            counter => counter.Key,
                            counter => new PersistedCounter(counter.Value.Positive, counter.Value.Negative),
                            StringComparer.Ordinal)),
                    StringComparer.Ordinal),
                new Dictionary<string, CartItemMetadata>(state.Metadata, StringComparer.Ordinal)),
            SerializerOptions);

    private static CartCrdtState FromLegacyLines(IReadOnlyList<CartLineItem> lines)
    {
        var items = new Dictionary<string, CartItemCrdt>(StringComparer.Ordinal);
        var metadata = new Dictionary<string, CartItemMetadata>(StringComparer.Ordinal);

        foreach (var item in lines)
        {
            var dot = new CartDot("server", item.AddedAt.ToUnixTimeMilliseconds());
            items[item.Sku] = new CartItemCrdt(
                new HashSet<CartDot> { dot },
                new HashSet<CartDot>(),
                new Dictionary<string, (long, long)> { ["server"] = (item.Quantity, 0) });
            metadata[item.Sku] = new CartItemMetadata(
                item.ProductName,
                item.UnitPrice,
                item.Currency,
                item.AddedAt);
        }

        return new CartCrdtState(items, metadata);
    }

    private sealed record PersistedCartCrdtState(
        Dictionary<string, PersistedCartItemCrdt> Items,
        Dictionary<string, CartItemMetadata> Metadata);

    private sealed record PersistedCartItemCrdt(
        HashSet<CartDot> LiveDots,
        HashSet<CartDot> TombstoneDots,
        Dictionary<string, PersistedCounter> Counters);

    private sealed record PersistedCounter(long Positive, long Negative);
}
