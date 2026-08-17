using System.Diagnostics;

namespace Inventory.UnitTests;

/// <summary>The throughput case for escrow, measured rather than assumed: an in-memory lock stands in for a database row lock to compare one shared SKU-wide lock against N per-bucket locks.</summary>
public class StockEscrowConcurrencyTests
{
    private static readonly TimeSpan SimulatedRowRoundTrip = TimeSpan.FromMilliseconds(2);

    [Fact]
    public void BucketedLockingCompletesTheSameWorkFasterThanOneLockForTheWholeSku()
    {
        const int bucketCount = 8;
        const int operationsPerBucket = 6;
        var totalOperations = bucketCount * operationsPerBucket;

        ThreadPool.SetMinThreads(bucketCount + 2, bucketCount + 2);

        const int sampleCount = 5;
        var singleLockElapsed = Median(Enumerable.Range(0, sampleCount)
            .Select(_ => RunWithLocking(totalOperations, lockCount: 1)));
        var bucketedElapsed = Median(Enumerable.Range(0, sampleCount)
            .Select(_ => RunWithLocking(totalOperations, lockCount: bucketCount)));

        Assert.True(
            bucketedElapsed < singleLockElapsed / 2,
            $"expected bucketed locking to meaningfully outperform a single lock; single={singleLockElapsed.TotalMilliseconds:0}ms, bucketed={bucketedElapsed.TotalMilliseconds:0}ms (medians of {sampleCount} runs)");
    }

    private static TimeSpan Median(IEnumerable<TimeSpan> samples)
    {
        var sorted = samples.OrderBy(sample => sample).ToArray();
        return sorted[sorted.Length / 2];
    }

    /// <param name="totalOperations">Total simulated operations to run across all locks.</param>
    /// <param name="lockCount">1 models today's one-row-per-SKU design; N models N escrow buckets.</param>
    /// <returns>Elapsed wall-clock time for the run.</returns>
    private static TimeSpan RunWithLocking(int totalOperations, int lockCount)
    {
        var locks = Enumerable.Range(0, lockCount).Select(_ => new object()).ToArray();
        var stopwatch = Stopwatch.StartNew();

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
