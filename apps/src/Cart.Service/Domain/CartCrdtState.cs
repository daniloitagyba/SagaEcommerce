namespace Cart.Service.Domain;

/// <summary>
/// Everything about a SKU that isn't its quantity or its
/// presence - snapshotted once, at first add, and never merged, since
/// there is no meaningful "join" of two product names. First writer wins;
/// re-adding after a full removal re-snapshots.
/// </summary>
public sealed record CartItemMetadata(string ProductName, decimal UnitPrice, string Currency, DateTimeOffset AddedAt);

/// <summary>
/// A whole cart as a map from Sku to its own
/// <see cref="CartItemCrdt"/> - the CRDT composition rule for maps
/// (Shapiro et al.'s composable/delta-CRDT framework; also how Riak's Map
/// and Akka's ORMap work) is simply "merge key-wise, union the keyspace" -
/// nothing about combining independent per-key CRDTs into one that covers
/// the whole map needs its own proof once each value type's join is
/// already proven, which is why CartItemCrdtPropertyTests only has to
/// prove the laws once, per item, not once per cart shape.
/// </summary>
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
            // Only snapshotted the first time a SKU becomes present -
            // re-increasing an already-live line must not silently
            // overwrite the price the shopper saw when they first added it.
            metadata = new Dictionary<string, CartItemMetadata>(Metadata, StringComparer.Ordinal) { [sku] = metadataIfNew };
        }

        return this with { Items = items, Metadata = metadata };
    }

    /// <summary>
    /// Works even when this state has never seen the SKU before (starts
    /// from CartItemCrdt.Empty, same as Increase does) - a client folding
    /// its own offline operations into a state to submit for merge may
    /// well decrease a SKU it never had reason to Increase in this same
    /// batch, since the server already knows about it from an earlier
    /// session. Decreasing an empty/never-added item is harmless: it just
    /// records a negative contribution with nothing present to subtract from yet.
    /// </summary>
    public CartCrdtState Decrease(string sku, string replicaId, long delta)
    {
        var current = Items.GetValueOrDefault(sku, CartItemCrdt.Empty);
        var items = new Dictionary<string, CartItemCrdt>(Items, StringComparer.Ordinal) { [sku] = current.Decrease(replicaId, delta) };
        return this with { Items = items };
    }

    /// <summary>Same reasoning as Decrease - a Remove for a SKU this state has never seen still needs to record something to merge against.</summary>
    public CartCrdtState Remove(string sku)
    {
        var current = Items.GetValueOrDefault(sku, CartItemCrdt.Empty);
        var items = new Dictionary<string, CartItemCrdt>(Items, StringComparer.Ordinal) { [sku] = current.Remove() };
        return this with { Items = items };
    }

    /// <summary>
    /// Removes the dots a client observed in an earlier snapshot. Shipping
    /// this causal context back with an offline remove is what distinguishes
    /// "remove the item I saw" from "this replica has never seen the item".
    /// Fresh concurrent dots are intentionally not tombstoned, preserving
    /// the cart's add-wins policy.
    /// </summary>
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

    /// <summary>
    /// Overwrites a present SKU's snapshotted price/name with a fresh one,
    /// leaving the CRDT quantity state (Items) completely untouched - the
    /// one deliberate exception to "snapshotted once, never merged"
    /// (CartItemMetadata's own comment), for the shopper who explicitly
    /// asked to see today's price rather than the one they saw when they
    /// added the line. Distinct from Merge's first-writer-wins TryAdd:
    /// this is a direct, local overwrite the caller asked for, not a join
    /// against another replica's state. A no-op (returns this unchanged)
    /// for a SKU that isn't currently present - refreshing a price for a
    /// line that isn't there has nothing to refresh.
    /// </summary>
    public CartCrdtState RefreshMetadata(string sku, CartItemMetadata metadata)
    {
        if (!Items.TryGetValue(sku, out var item) || !item.IsPresent)
        {
            return this;
        }

        var metadataMap = new Dictionary<string, CartItemMetadata>(Metadata, StringComparer.Ordinal) { [sku] = metadata };
        return this with { Metadata = metadataMap };
    }

    /// <summary>The join, lifted key-wise from <see cref="CartItemCrdt.Merge"/> - see the class comment for why that's sufficient.</summary>
    public static CartCrdtState Merge(CartCrdtState a, CartCrdtState b)
    {
        var items = new Dictionary<string, CartItemCrdt>(a.Items, StringComparer.Ordinal);
        foreach (var (sku, item) in b.Items)
        {
            items[sku] = items.TryGetValue(sku, out var existing) ? CartItemCrdt.Merge(existing, item) : item;
        }

        // First writer wins per SKU, not a real join - see CartItemMetadata's own comment on why that's fine here.
        var metadata = new Dictionary<string, CartItemMetadata>(a.Metadata, StringComparer.Ordinal);
        foreach (var (sku, entry) in b.Metadata)
        {
            metadata.TryAdd(sku, entry);
        }

        return new CartCrdtState(items, metadata);
    }

    /// <summary>The read view every API response actually serves - only SKUs the CRDT currently considers present, in the shape Storefront/checkout already expect.</summary>
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
