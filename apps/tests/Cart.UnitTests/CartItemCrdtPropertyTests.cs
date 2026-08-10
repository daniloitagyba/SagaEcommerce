using Cart.Service.Domain;
using CsCheck;

namespace Cart.UnitTests;

/// <summary>
/// The actual mathematical content of "this is a CRDT" -
/// commutativity, associativity and idempotence of Merge, proven against
/// randomly generated states rather than asserted by inspection, plus the
/// two behavioural properties the whole exercise exists for: no
/// resurrection, and add-wins-over-a-concurrent-remove.
///
/// record-synthesized Equals does not help here - HashSet and Dictionary
/// don't override Equals (reference equality), so two independently built
/// CartItemCrdt values with identical content compare unequal by the
/// language's own default. StructurallyEqual below is this file's own
/// comparer, used everywhere Assert.Equal would otherwise silently pass or
/// fail for the wrong reason.
/// </summary>
public class CartItemCrdtPropertyTests
{
    private static bool StructurallyEqual(CartItemCrdt a, CartItemCrdt b) =>
        a.LiveDots.SetEquals(b.LiveDots)
        && a.TombstoneDots.SetEquals(b.TombstoneDots)
        && a.Counters.Count == b.Counters.Count
        && a.Counters.All(entry => b.Counters.TryGetValue(entry.Key, out var other) && entry.Value == other);

    private static Gen<CartDot> GenDot =>
        Gen.Select(Gen.OneOfConst("replica-a", "replica-b", "replica-c"), Gen.Long[0, 1000])
            .Select(t => new CartDot(t.Item1, t.Item2));

    /// <summary>
    /// Builds an arbitrary reachable CartItemCrdt by folding a random
    /// sequence of Increase/Decrease/Remove operations from a small pool of
    /// replicas - not an arbitrary bag of fields, which could describe a
    /// state Merge could never actually produce.
    /// </summary>
    private static Gen<CartItemCrdt> GenState =>
        Gen.Select(Gen.OneOfConst("replica-a", "replica-b", "replica-c"), Gen.Int[1, 20], Gen.Int[0, 2])
            .List[0, 15]
            .Select(operations =>
            {
                var state = CartItemCrdt.Empty;
                long dotCounter = 0;
                foreach (var (replicaId, delta, kind) in operations)
                {
                    state = kind switch
                    {
                        0 => state.Increase(replicaId, delta, dotCounter++),
                        1 => state.Decrease(replicaId, delta),
                        _ => state.Remove()
                    };
                }

                return state;
            });

    [Fact]
    public void MergeIsCommutative()
    {
        Gen.Select(GenState, GenState).Sample(t =>
        {
            var (a, b) = t;
            return StructurallyEqual(CartItemCrdt.Merge(a, b), CartItemCrdt.Merge(b, a));
        }, iter: 2_000);
    }

    [Fact]
    public void MergeIsAssociative()
    {
        Gen.Select(GenState, GenState, GenState).Sample(t =>
        {
            var (a, b, c) = t;
            var left = CartItemCrdt.Merge(CartItemCrdt.Merge(a, b), c);
            var right = CartItemCrdt.Merge(a, CartItemCrdt.Merge(b, c));
            return StructurallyEqual(left, right);
        }, iter: 2_000);
    }

    [Fact]
    public void MergeIsIdempotent()
    {
        GenState.Sample(a => StructurallyEqual(CartItemCrdt.Merge(a, a), a), iter: 2_000);
    }

    [Fact]
    public void MergingWithEmptyIsANoOp()
    {
        GenState.Sample(a =>
            StructurallyEqual(CartItemCrdt.Merge(a, CartItemCrdt.Empty), a)
            && StructurallyEqual(CartItemCrdt.Merge(CartItemCrdt.Empty, a), a),
            iter: 2_000);
    }

    [Fact]
    public void EffectiveQuantityIsNeverNegativeRegardlessOfMergeOrder()
    {
        Gen.Select(GenState, GenState).Sample(t =>
        {
            var (a, b) = t;
            return CartItemCrdt.Merge(a, b).EffectiveQuantity >= 0;
        }, iter: 2_000);
    }

    [Fact]
    public void SequentialAddThenRemoveOnTheSameReplicaNeverResurrectsUnderAnyMerge()
    {
        // The canonical case: one replica adds, then removes, entirely on
        // its own - a second replica that never touched this SKU merging
        // in afterwards must never bring it back.
        Gen.Select(GenState, Gen.Int[1, 20]).Sample(t =>
        {
            var (untouched, quantity) = t;

            var addedThenRemoved = CartItemCrdt.Empty.Increase("replica-a", quantity, dotCounter: 0).Remove();

            var merged = CartItemCrdt.Merge(addedThenRemoved, untouched);
            return !merged.IsPresent || untouched.IsPresent;
        }, iter: 2_000);
    }

    [Fact]
    public void AConcurrentAddSurvivesARemoveThatNeverObservedIt()
    {
        // Replica B removes everything it has seen (nothing, in this case -
        // it starts from empty, modelling "never synced this SKU").
        // Replica A concurrently adds. The dot A minted was never in B's
        // tombstone set, so it must survive the merge - add wins.
        Gen.Int[1, 50].Sample(quantity =>
        {
            var replicaARemoved = CartItemCrdt.Empty.Remove(); // observed nothing, tombstones nothing
            var replicaBAdded = CartItemCrdt.Empty.Increase("replica-b", quantity, dotCounter: 0);

            var merged = CartItemCrdt.Merge(replicaARemoved, replicaBAdded);
            return merged.IsPresent && merged.EffectiveQuantity == quantity;
        }, iter: 500);
    }

    [Fact]
    public void AConcurrentAddSurvivesARemoveOfAnEarlierVersionOfTheSameItem()
    {
        // The Dynamo-paper shopping-cart scenario itself: the item already
        // exists (dot 0). Replica A observes it and removes it (tombstones
        // dot 0). Concurrently, replica B increases the same SKU - a fresh
        // dot (1) neither replica has reconciled yet. After merging, the
        // increase must survive: A's remove could only tombstone what A had
        // actually seen, and dot 1 postdates that.
        var original = CartItemCrdt.Empty.Increase("replica-a", 1, dotCounter: 0);

        var replicaARemoved = original.Remove();
        var replicaBIncreased = original.Increase("replica-b", 2, dotCounter: 1);

        var merged = CartItemCrdt.Merge(replicaARemoved, replicaBIncreased);

        Assert.True(merged.IsPresent);
        Assert.Equal(3, merged.EffectiveQuantity); // 1 (original) + 2 (B's increase) - A's remove only tombstoned dot 0's original contribution to *presence*, not the counter.
    }
}
