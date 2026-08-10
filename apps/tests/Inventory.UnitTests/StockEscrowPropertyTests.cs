using CsCheck;
using Inventory.Service.Domain;

namespace Inventory.UnitTests;

/// <summary>
/// StockEscrow's core promise, proven rather than asserted -
/// splitting one counter into N buckets must never let more out than the
/// original total held, regardless of how requests are routed or how many
/// of them run.
/// </summary>
public class StockEscrowPropertyTests
{
    [Fact]
    public void SplitPreservesTheTotalExactly()
    {
        Gen.Select(Gen.Int[0, 10_000], Gen.Int[1, 32]).Sample(t =>
        {
            var (total, buckets) = t;
            return StockEscrow.Split(total, buckets).Total == total;
        }, iter: 2_000);
    }

    [Fact]
    public void NoBucketDiffersFromAnotherByMoreThanOneUnit()
    {
        Gen.Select(Gen.Int[0, 10_000], Gen.Int[1, 32]).Sample(t =>
        {
            var (total, bucketCount) = t;
            var buckets = StockEscrow.Split(total, bucketCount).Buckets;
            return buckets.Max() - buckets.Min() <= 1;
        }, iter: 2_000);
    }

    [Fact]
    public void AReservationWithinTheTotalIsAlwaysFulfillableFromSomeCombinationOfBuckets()
    {
        Gen.Select(Gen.Int[1, 500], Gen.Int[1, 16], Gen.Int[0, 15])
            .SelectMany(t => Gen.Int[1, t.Item1].Select(requested => (t.Item1, t.Item2, t.Item3, requested)))
            .Sample(t =>
            {
                var (total, bucketCount, preferred, requested) = t;
                var escrow = StockEscrow.Split(total, bucketCount);
                var plan = escrow.TryReserve(preferred % bucketCount, requested);

                return plan.Fulfillable && plan.Lines.Sum(line => line.Quantity) == requested;
            }, iter: 5_000);
    }

    [Fact]
    public void AReservationExceedingTheTotalIsNeverFulfillable()
    {
        Gen.Select(Gen.Int[0, 500], Gen.Int[1, 16]).Sample(t =>
        {
            var (total, bucketCount) = t;
            var escrow = StockEscrow.Split(total, bucketCount);
            var plan = escrow.TryReserve(0, total + 1);
            return !plan.Fulfillable;
        }, iter: 2_000);
    }

    [Fact]
    public void ApplyingAPlanNeverDrivesTheGrandTotalBelowZeroOrAboveTheOriginal()
    {
        Gen.Select(Gen.Int[1, 500], Gen.Int[1, 16], Gen.Int[0, 15])
            .SelectMany(t => Gen.Int[1, t.Item1].Select(requested => (t.Item1, t.Item2, t.Item3, requested)))
            .Sample(t =>
            {
                var (total, bucketCount, preferred, requested) = t;
                var escrow = StockEscrow.Split(total, bucketCount);
                var plan = escrow.TryReserve(preferred % bucketCount, requested);
                if (!plan.Fulfillable)
                {
                    return true;
                }

                var afterReserve = escrow.Apply(plan.Lines);
                if (afterReserve.Total != total - requested || afterReserve.Total < 0)
                {
                    return false;
                }

                var afterRelease = afterReserve.Release(plan.Lines);
                return afterRelease.Total == total;
            }, iter: 5_000);
    }

    [Fact]
    public void ManySequentialSmallReservationsAcrossAllBucketsExactlyExhaustTheTotal()
    {
        // Deterministic, not concurrent - StockEscrow itself is immutable
        // and was never meant to be mutated in place under a race (see
        // StockEscrowConcurrencyTests for the actual concurrency story,
        // which lives at the level of whatever holds the authoritative
        // copy per bucket, not inside this type). What this pins is that
        // spreading many one-unit reservations across every bucket via
        // PreferredBucket, applied one at a time, drains the network to
        // precisely zero with no unit lost or double-counted along the way.
        const int bucketCount = 8;
        const int perBucket = 50;
        var totalUnits = bucketCount * perBucket;
        var escrow = StockEscrow.Split(totalUnits, bucketCount);

        for (var i = 0; i < totalUnits; i++)
        {
            var preferred = StockEscrow.PreferredBucket($"reservation-{i}", bucketCount);
            var plan = escrow.TryReserve(preferred, 1);
            Assert.True(plan.Fulfillable);
            escrow = escrow.Apply(plan.Lines);
        }

        Assert.Equal(0, escrow.Total);
        Assert.False(escrow.TryReserve(0, 1).Fulfillable);
    }
}
