using Cart.Service.Domain;
using CsCheck;

namespace Cart.UnitTests;

/// <summary>The mathematical content of "this is a CRDT" - commutativity, associativity and idempotence of Merge, proven against randomly generated states, plus the two behavioural properties the exercise exists for: no resurrection, and add-wins-over-a-concurrent-remove.</summary>
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

    /// <summary>Builds an arbitrary reachable CartItemCrdt by folding a random sequence of Increase/Decrease/Remove operations from a small pool of replicas - not an arbitrary bag of fields, which could describe a state Merge could never actually produce.</summary>
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
    public void EffectiveQuantitySaturatesAtTheHttpQuantityLimit()
    {
        var state = new CartItemCrdt(
            new HashSet<CartDot> { new("replica-a", 1) },
            new HashSet<CartDot>(),
            new Dictionary<string, (long Positive, long Negative)>
            {
                ["replica-a"] = (long.MaxValue, 0),
                ["replica-b"] = (long.MaxValue, 0)
            });

        Assert.Equal(int.MaxValue, state.EffectiveQuantity);
    }

    [Fact]
    public void CounterMutationSaturatesInsteadOfOverflowing()
    {
        var state = CartItemCrdt.Empty
            .Increase("replica-a", long.MaxValue, dotCounter: 1)
            .Increase("replica-a", 1, dotCounter: 2);

        Assert.Equal(long.MaxValue, state.Counters["replica-a"].Positive);
        Assert.Equal(int.MaxValue, state.EffectiveQuantity);
    }

    [Fact]
    public void SequentialAddThenRemoveOnTheSameReplicaNeverResurrectsUnderAnyMerge()
    {
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
        Gen.Int[1, 50].Sample(quantity =>
        {
            var replicaARemoved = CartItemCrdt.Empty.Remove();
            var replicaBAdded = CartItemCrdt.Empty.Increase("replica-b", quantity, dotCounter: 0);

            var merged = CartItemCrdt.Merge(replicaARemoved, replicaBAdded);
            return merged.IsPresent && merged.EffectiveQuantity == quantity;
        }, iter: 500);
    }

    [Fact]
    public void AConcurrentAddSurvivesARemoveOfAnEarlierVersionOfTheSameItem()
    {
        var original = CartItemCrdt.Empty.Increase("replica-a", 1, dotCounter: 0);

        var replicaARemoved = original.Remove();
        var replicaBIncreased = original.Increase("replica-b", 2, dotCounter: 1);

        var merged = CartItemCrdt.Merge(replicaARemoved, replicaBIncreased);

        Assert.True(merged.IsPresent);
        Assert.Equal(3, merged.EffectiveQuantity);
    }
}
