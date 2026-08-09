using System.Diagnostics;

namespace Inventory.IntegrationTests;

/// <summary>
/// The throughput case for escrow, measured rather than
/// assumed - the same discipline used to establish the
/// ceiling this answers. Neither side here calls into
/// Inventory.Service's real Kafka/Postgres path (there is no live cluster
/// in this environment to measure against); both simulate the *locking
/// shape* each design implies with an in-memory lock standing in for a
/// database row lock, and a fixed synthetic delay standing in for the
/// round trip a real row-level UPDATE would take. What's real is the
/// difference the shape itself produces: one shared lock for a whole SKU
/// serializes every reservation regardless of how many rows exist behind
/// it; N per-bucket locks let up to N reservations proceed at once.
/// </summary>
public class StockEscrowConcurrencyTests
{
    // Large enough that thread-scheduling noise on a loaded CI runner
    // cannot plausibly account for the measured difference; small enough
    // that the whole test still runs in well under a second.
    private static readonly TimeSpan SimulatedRowRoundTrip = TimeSpan.FromMilliseconds(2);

    [Fact]
    public void BucketedLockingCompletesTheSameWorkFasterThanOneLockForTheWholeSku()
    {
        const int bucketCount = 8;
        const int operationsPerBucket = 6;
        var totalOperations = bucketCount * operationsPerBucket;

        var singleLockElapsed = RunWithLocking(totalOperations, lockCount: 1);
        var bucketedElapsed = RunWithLocking(totalOperations, lockCount: bucketCount);

        // Not a specific multiplier - bucketCount-fold speedup is the
        // theoretical ceiling, never the measured floor once real
        // scheduling and Parallel.For's own overhead are in the picture.
        // A comfortable fraction of it is enough to demonstrate the shape
        // is real without the test being one slow CI runner away from flaking.
        Assert.True(
            bucketedElapsed < singleLockElapsed / 2,
            $"expected bucketed locking to meaningfully outperform a single lock; single={singleLockElapsed.TotalMilliseconds:0}ms, bucketed={bucketedElapsed.TotalMilliseconds:0}ms");
    }

    /// <summary>
    /// <paramref name="lockCount"/> = 1 models today's one-row-per-SKU
    /// design (the partition-per-SKU guarantee makes the lock
    /// implicit rather than explicit, but the effect - one operation at a
    /// time for this SKU, network-wide - is identical). lockCount = N
    /// models N escrow buckets, each an independent row a real deployment
    /// would let a different partition/consumer own.
    /// </summary>
    private static TimeSpan RunWithLocking(int totalOperations, int lockCount)
    {
        var locks = Enumerable.Range(0, lockCount).Select(_ => new object()).ToArray();
        var stopwatch = Stopwatch.StartNew();

        // MaxDegreeOfParallelism explicit, not defaulted: Parallel.For
        // otherwise caps concurrency at Environment.ProcessorCount
        // regardless of how many locks exist, which on a 2-vCPU CI runner
        // made the "bucketed" case just as serialized as the single-lock
        // one - the test wasn't measuring the locking shape any more, it
        // was measuring the runner's core count. The work itself is
        // Thread.Sleep, not CPU-bound, so the thread pool can genuinely
        // run this many concurrently regardless of core count.
        var options = new ParallelOptions { MaxDegreeOfParallelism = lockCount };

        Parallel.For(0, totalOperations, options, i =>
        {
            lock (locks[i % lockCount])
            {
                Thread.Sleep(SimulatedRowRoundTrip);
            }
        });

        stopwatch.Stop();
        return stopwatch.Elapsed;
    }
}
