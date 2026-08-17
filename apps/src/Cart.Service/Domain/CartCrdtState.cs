namespace Cart.Service.Domain;

/// <summary>Everything about a SKU that isn't quantity or presence; snapshotted once at first add and never merged.</summary>
public sealed record CartItemMetadata(string ProductName, decimal UnitPrice, string Currency, DateTimeOffset AddedAt);

/// <summary>A whole cart as a map from Sku to its own <see cref="CartItemCrdt"/>, merged key-wise.</summary>
public sealed record CartCrdtState(
    IReadOnlyDictionary<string, CartItemCrdt> Items,
    IReadOnlyDictionary<string, CartItemMetadata> Metadata)
{
    public static readonly CartCrdtState Empty = new(
        new Dictionary<string, CartItemCrdt>(StringComparer.Ordinal),
        new Dictionary<string, CartItemMetadata>(StringComparer.Ordinal));

    public CartCrdtState Increase(string sku, string replicaId, long delta, long dotCounter, CartItemMetadata? metadataIfNew)
    {
        var current = Items.GetValueOrDefault(sku, CartItemCrdt.Empty);
        var items = new Dictionary<string, CartItemCrdt>(Items, StringComparer.Ordinal)
        {
            [sku] = current.Increase(replicaId, delta, dotCounter)
        };

        var metadata = Metadata;
        if (metadataIfNew is not null && !current.IsPresent)
        {
            metadata = new Dictionary<string, CartItemMetadata>(Metadata, StringComparer.Ordinal) { [sku] = metadataIfNew };
        }

        return this with { Items = items, Metadata = metadata };
    }

    /// <summary>Decreases a SKU's quantity, working even when this state has never seen the SKU before.</summary>
    public CartCrdtState Decrease(string sku, string replicaId, long delta)
    {
        var current = Items.GetValueOrDefault(sku, CartItemCrdt.Empty);
        var items = new Dictionary<string, CartItemCrdt>(Items, StringComparer.Ordinal) { [sku] = current.Decrease(replicaId, delta) };
        return this with { Items = items };
    }

    /// <summary>Removes a SKU, recording a tombstone even if this state has never seen it before.</summary>
    public CartCrdtState Remove(string sku)
    {
        var current = Items.GetValueOrDefault(sku, CartItemCrdt.Empty);
        var items = new Dictionary<string, CartItemCrdt>(Items, StringComparer.Ordinal) { [sku] = current.Remove() };
        return this with { Items = items };
    }

    /// <summary>Removes the dots a client observed in an earlier snapshot, preserving add-wins for concurrent fresh dots.</summary>
    public CartCrdtState RemoveObserved(string sku, IEnumerable<CartDot> observedDots)
    {
        var current = Items.GetValueOrDefault(sku, CartItemCrdt.Empty);
        var liveDots = new HashSet<CartDot>(current.LiveDots);
        liveDots.UnionWith(observedDots);
        var observed = current with { LiveDots = liveDots };
        var items = new Dictionary<string, CartItemCrdt>(Items, StringComparer.Ordinal)
        {
            [sku] = observed.Remove()
        };
        return this with { Items = items };
    }

    /// <summary>Overwrites a present SKU's snapshotted price/name, leaving CRDT quantity state untouched; a no-op if absent.</summary>
    public CartCrdtState RefreshMetadata(string sku, CartItemMetadata metadata)
    {
        if (!Items.TryGetValue(sku, out var item) || !item.IsPresent)
        {
            return this;
        }

        var metadataMap = new Dictionary<string, CartItemMetadata>(Metadata, StringComparer.Ordinal) { [sku] = metadata };
        return this with { Metadata = metadataMap };
    }

    /// <summary>The join, lifted key-wise from <see cref="CartItemCrdt.Merge"/>.</summary>
    public static CartCrdtState Merge(CartCrdtState a, CartCrdtState b)
    {
        var items = new Dictionary<string, CartItemCrdt>(a.Items, StringComparer.Ordinal);
        foreach (var (sku, item) in b.Items)
        {
            items[sku] = items.TryGetValue(sku, out var existing) ? CartItemCrdt.Merge(existing, item) : item;
        }

        var metadata = new Dictionary<string, CartItemMetadata>(a.Metadata, StringComparer.Ordinal);
        foreach (var (sku, entry) in b.Metadata)
        {
            metadata.TryAdd(sku, entry);
        }

        return new CartCrdtState(items, metadata);
    }

    /// <summary>Returns only SKUs the CRDT currently considers present, as line items.</summary>
    public IReadOnlyList<CartLineItem> ToLineItems()
    {
        var result = new List<CartLineItem>();
        foreach (var (sku, item) in Items)
        {
            if (!item.IsPresent || !Metadata.TryGetValue(sku, out var metadata))
            {
                continue;
            }

            result.Add(new CartLineItem(sku, item.EffectiveQuantity, metadata.UnitPrice, metadata.Currency, metadata.ProductName, metadata.AddedAt));
        }

        return result;
    }
}
