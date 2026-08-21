namespace Cart.Service.Domain;

/// <summary>One event from one replica; uniquely identifies an "add or increase" so a merge can tell whether it was observed by a remove.</summary>
public readonly record struct CartDot(string ReplicaId, long Counter);

/// <summary>One SKU's state as an Add-Wins Observed-Remove Set composed with a PN-Counter for quantity.</summary>
public sealed record CartItemCrdt(
    IReadOnlySet<CartDot> LiveDots,
    IReadOnlySet<CartDot> TombstoneDots,
    IReadOnlyDictionary<string, (long Positive, long Negative)> Counters)
{
    public static readonly CartItemCrdt Empty = new(
        new HashSet<CartDot>(), new HashSet<CartDot>(), new Dictionary<string, (long, long)>());

    /// <summary>Whether this SKU belongs in the cart at all - distinct from quantity, which can be 0 mid-merge and still be "present."</summary>
    public bool IsPresent => LiveDots.Count > 0;

    /// <summary>Net of every replica's contributions, clamped to the HTTP quantity range so a hostile or corrupted CRDT state cannot overflow an API response.</summary>
    public int EffectiveQuantity
    {
        get
        {
            var total = Counters.Values.Aggregate(
                0m,
                static (current, counter) => current + counter.Positive - counter.Negative);
            return total <= 0m
                ? 0
                : total >= int.MaxValue
                    ? int.MaxValue
                    : decimal.ToInt32(decimal.Truncate(total));
        }
    }

    /// <summary>An add or an increase; both mint a fresh dot so the assertion survives a concurrent remove.</summary>
    public CartItemCrdt Increase(string replicaId, long delta, long dotCounter)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(delta);

        var liveDots = new HashSet<CartDot>(LiveDots) { new(replicaId, dotCounter) };
        var counters = new Dictionary<string, (long Positive, long Negative)>(Counters);
        var (positive, negative) = counters.GetValueOrDefault(replicaId);
        counters[replicaId] = (SaturatingAdd(positive, delta), negative);

        return this with { LiveDots = liveDots, Counters = counters };
    }

    /// <summary>A partial reduction in quantity; does not touch presence.</summary>
    public CartItemCrdt Decrease(string replicaId, long delta)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(delta);

        var counters = new Dictionary<string, (long Positive, long Negative)>(Counters);
        var (positive, negative) = counters.GetValueOrDefault(replicaId);
        counters[replicaId] = (positive, SaturatingAdd(negative, delta));

        return this with { Counters = counters };
    }

    /// <summary>Tombstones every dot this replica currently observes as live.</summary>
    public CartItemCrdt Remove()
    {
        var tombstoneDots = new HashSet<CartDot>(TombstoneDots);
        tombstoneDots.UnionWith(LiveDots);

        return this with { LiveDots = new HashSet<CartDot>(), TombstoneDots = tombstoneDots };
    }

    /// <summary>The CRDT join: commutative, associative, and idempotent.</summary>
    public static CartItemCrdt Merge(CartItemCrdt a, CartItemCrdt b)
    {
        var tombstoneDots = new HashSet<CartDot>(a.TombstoneDots);
        tombstoneDots.UnionWith(b.TombstoneDots);

        var liveDots = new HashSet<CartDot>(a.LiveDots);
        liveDots.UnionWith(b.LiveDots);
        liveDots.ExceptWith(tombstoneDots);

        var counters = new Dictionary<string, (long Positive, long Negative)>(a.Counters);
        foreach (var (replicaId, (positive, negative)) in b.Counters)
        {
            var (existingPositive, existingNegative) = counters.GetValueOrDefault(replicaId);
            counters[replicaId] = (Math.Max(existingPositive, positive), Math.Max(existingNegative, negative));
        }

        return new CartItemCrdt(liveDots, tombstoneDots, counters);
    }

    private static long SaturatingAdd(long value, long delta) => value > long.MaxValue - delta
        ? long.MaxValue
        : value + delta;
}
