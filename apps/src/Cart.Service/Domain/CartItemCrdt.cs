namespace Cart.Service.Domain;

/// <summary>
/// Milestone 86: one event, from one replica - a device, a tab, a retried
/// request. Uniquely identifies an "add or increase" so a merge can tell
/// whether a given add was ever observed by a given remove, which is the
/// entire mechanism behind add-wins: a remove can only tombstone dots it
/// has actually seen, so a concurrent add's fresh dot survives it.
/// </summary>
public readonly record struct CartDot(string ReplicaId, long Counter);

/// <summary>
/// Milestone 86: one SKU's state as an Add-Wins Observed-Remove Set
/// (Bieniusa et al.; the same shape Riak and Akka's ORSet use) composed
/// with a PN-Counter for quantity - the CRDT this lab's Dynamo-paper-style
/// cart problem calls for. Two properties fall out of the join
/// (<see cref="Merge"/>) rather than being special-cased:
///
/// <list type="bullet">
/// <item><b>No resurrection.</b> The classic OR-Set bug (a naive
/// grow-only set, or last-write-wins on a single field) can bring back an
/// item a replica already removed, or drop one it never got to see added.
/// Tombstones are dots actually observed at removal time, not "the SKU is
/// absent" as a fact - a concurrent add's dot was never observed, so it is
/// never in anyone's tombstone set, and survives the merge.</item>
/// <item><b>Add wins over a concurrent remove.</b> If replica A adds
/// (mints a fresh dot) at the same causal moment replica B removes
/// everything B has seen so far, B's removal cannot have tombstoned a dot
/// it never observed. After merging, A's dot is live. This is a
/// deliberate policy choice (not the only reasonable one - remove-wins
/// OR-Sets exist too) and the right default for a cart: losing an item a
/// shopper just added because another tab happened to clear the cart a
/// moment earlier is the worse failure mode of the two for e-commerce.</item>
/// </list>
///
/// Quantity is a PN-Counter, not a last-write-wins value, for the same
/// reason: "set the quantity to 3" is really "increase by 2 from what I
/// last saw," and two concurrent increases from different replicas should
/// both count, not have one silently overwrite the other.
/// </summary>
public sealed record CartItemCrdt(
    IReadOnlySet<CartDot> LiveDots,
    IReadOnlySet<CartDot> TombstoneDots,
    IReadOnlyDictionary<string, (long Positive, long Negative)> Counters)
{
    public static readonly CartItemCrdt Empty = new(
        new HashSet<CartDot>(), new HashSet<CartDot>(), new Dictionary<string, (long, long)>());

    /// <summary>Whether this SKU belongs in the cart at all - distinct from quantity, which can be 0 mid-merge and still be "present."</summary>
    public bool IsPresent => LiveDots.Count > 0;

    /// <summary>Net of every replica's contributions, never negative - a PN-Counter can transiently sum negative under a partial merge, which a shopper must never see as a negative line.</summary>
    public int EffectiveQuantity => (int)Math.Max(0, Counters.Values.Sum(counter => counter.Positive - counter.Negative));

    /// <summary>
    /// An add, or an increase - both mint a fresh dot, since both are "this
    /// replica asserts more of this SKU belongs in the cart," and a fresh
    /// dot is exactly what lets that assertion survive a remove that
    /// happened concurrently elsewhere and could not have observed it.
    /// </summary>
    public CartItemCrdt Increase(string replicaId, long delta, long dotCounter)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(delta);

        var liveDots = new HashSet<CartDot>(LiveDots) { new(replicaId, dotCounter) };
        var counters = new Dictionary<string, (long Positive, long Negative)>(Counters);
        var (positive, negative) = counters.GetValueOrDefault(replicaId);
        counters[replicaId] = (positive + delta, negative);

        return this with { LiveDots = liveDots, Counters = counters };
    }

    /// <summary>
    /// A partial reduction in quantity - does not touch presence. Removing
    /// the SKU entirely is <see cref="Remove"/>, a different operation:
    /// "fewer of these" and "none of these" merge differently, since the
    /// latter has to tombstone dots for add-wins to mean anything.
    /// </summary>
    public CartItemCrdt Decrease(string replicaId, long delta)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(delta);

        var counters = new Dictionary<string, (long Positive, long Negative)>(Counters);
        var (positive, negative) = counters.GetValueOrDefault(replicaId);
        counters[replicaId] = (positive, negative + delta);

        return this with { Counters = counters };
    }

    /// <summary>
    /// Tombstones every dot this replica currently observes as live - not
    /// "mark absent," which a merge could not distinguish from "never
    /// existed here." A dot minted by a concurrent Increase this replica
    /// has not yet observed is, correctly, not among them. Takes no
    /// replica id: unlike Increase, a remove mints nothing of its own, it
    /// only tombstones dots someone else already minted.
    /// </summary>
    public CartItemCrdt Remove()
    {
        var tombstoneDots = new HashSet<CartDot>(TombstoneDots);
        tombstoneDots.UnionWith(LiveDots);

        return this with { LiveDots = new HashSet<CartDot>(), TombstoneDots = tombstoneDots };
    }

    /// <summary>
    /// The CRDT join: commutative, associative, idempotent (proven in
    /// CartItemCrdtPropertyTests) - the three laws that make "merge in any
    /// order, any number of times, and get the same answer" true regardless
    /// of network delivery order. Live = every dot either side has ever
    /// seen added, minus every dot either side has ever seen removed;
    /// counters merge component-wise by <see cref="Math.Max(long,long)"/>,
    /// the standard grow-only-counter join, applied to both the positive
    /// and negative halves of the PN-Counter independently.
    /// </summary>
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
}
