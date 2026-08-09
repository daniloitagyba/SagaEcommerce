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

        // The ThreadPool ramps up worker threads gradually (hill-climbing,
        // roughly one new thread per ~500ms under contention) rather than
        // handing out bucketCount threads the instant Parallel.For asks for
        // them. On a busy CI runner that ramp-up can eat into the bucketed
        // run's short measured window and make it look no faster than the
        // single-lock run - a starvation artifact, not the locking shape
        // this test exists to demonstrate. Forcing the minimum up front
        // makes bucketCount threads available immediately for both runs.
        ThreadPool.SetMinThreads(bucketCount + 2, bucketCount + 2);

        // A single sample of either side is noisy regardless of thread-pool
        // warm-up - a GC pause, a scheduler quantum, or another process
        // stealing CPU for a few milliseconds during just one of the two
        // runs is enough to blow the margin below, on any machine, not just
        // a slow one. The median of several independent samples is
        // insensitive to any one such outlier without hiding a real,
        // sustained difference in the locking shape.
        const int sampleCount = 5;
        var singleLockElapsed = Median(Enumerable.Range(0, sampleCount)
            .Select(_ => RunWithLocking(totalOperations, lockCount: 1)));
        var bucketedElapsed = Median(Enumerable.Range(0, sampleCount)
            .Select(_ => RunWithLocking(totalOperations, lockCount: bucketCount)));

        // Not a specific multiplier - bucketCount-fold speedup is the
        // theoretical ceiling, never the measured floor once real
        // scheduling and Parallel.For's own overhead are in the picture.
        // A comfortable fraction of it is enough to demonstrate the shape
        // is real without the test being one slow CI runner away from flaking.
        Assert.True(
            bucketedElapsed < singleLockElapsed / 2,
            $"expected bucketed locking to meaningfully outperform a single lock; single={singleLockElapsed.TotalMilliseconds:0}ms, bucketed={bucketedElapsed.TotalMilliseconds:0}ms (medians of {sampleCount} runs)");
    }

    private static TimeSpan Median(IEnumerable<TimeSpan> samples)
    {
        var sorted = samples.OrderBy(sample => sample).ToArray();
        return sorted[sorted.Length / 2];
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
