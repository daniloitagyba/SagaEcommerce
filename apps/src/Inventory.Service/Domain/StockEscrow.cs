namespace Inventory.Service.Domain;

/// <summary>Splits one warehouse's stock of one SKU into N independently-lockable buckets so concurrent reservations can be decided in parallel.</summary>
public sealed class StockEscrow
{
    private readonly IReadOnlyList<int> _buckets;

    private StockEscrow(IReadOnlyList<int> buckets)
    {
        _buckets = buckets;
    }

    public int BucketCount => _buckets.Count;

    public IReadOnlyList<int> Buckets => _buckets;

    public int Total => _buckets.Sum();

    /// <summary>Splits a total evenly; a remainder that doesn't divide cleanly goes to the first buckets, one each - no bucket differs from another by more than one unit.</summary>
    public static StockEscrow Split(int totalAvailable, int bucketCount)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(totalAvailable);
        ArgumentOutOfRangeException.ThrowIfLessThan(bucketCount, 1);

        var (baseAmount, remainder) = Math.DivRem(totalAvailable, bucketCount);
        var buckets = new int[bucketCount];
        for (var i = 0; i < bucketCount; i++)
        {
            buckets[i] = baseAmount + (i < remainder ? 1 : 0);
        }

        return new StockEscrow(buckets);
    }

    private static StockEscrow FromBuckets(IReadOnlyList<int> buckets) => new(buckets);

    /// <summary>Consistent hash routing so the same key always prefers the same bucket.</summary>
    public static int PreferredBucket(string key, int bucketCount) =>
        (int)((uint)key.GetHashCode() % (uint)bucketCount);

    public sealed record ReservationLine(int Bucket, int Quantity);

    public sealed record ReservationPlan(bool Fulfillable, IReadOnlyList<ReservationLine> Lines)
    {
        public static ReservationPlan Unfulfillable => new(false, []);
    }

    /// <summary>Tries to fill <paramref name="quantity"/> starting from <paramref name="preferredBucket"/>, borrowing from nearest siblings; does not mutate <c>this</c>.</summary>
    public ReservationPlan TryReserve(int preferredBucket, int quantity)
    {
        if (preferredBucket < 0 || preferredBucket >= BucketCount)
        {
            throw new ArgumentOutOfRangeException(nameof(preferredBucket));
        }

        var candidates = new List<StockAllocator.Candidate>(BucketCount);
        for (var i = 0; i < BucketCount; i++)
        {
            candidates.Add(new StockAllocator.Candidate(
                i.ToString(System.Globalization.CultureInfo.InvariantCulture),
                _buckets[i],
                CircularDistance(preferredBucket, i, BucketCount)));
        }

        var plan = StockAllocator.Allocate(candidates, quantity);
        if (!plan.Fulfillable)
        {
            return ReservationPlan.Unfulfillable;
        }

        var lines = plan.Lines
            .Select(line => new ReservationLine(int.Parse(line.WarehouseCode, System.Globalization.CultureInfo.InvariantCulture), line.Quantity))
            .ToList();
        return new ReservationPlan(true, lines);
    }

    /// <summary>Draws down exactly what a prior TryReserve plan named - never re-derives which buckets to touch, the same reasoning WarehouseAllocationStore's own allocations already follow.</summary>
    public StockEscrow Apply(IReadOnlyList<ReservationLine> lines)
    {
        var buckets = _buckets.ToArray();
        foreach (var line in lines)
        {
            if (line.Quantity < 0 || line.Bucket < 0 || line.Bucket >= buckets.Length || buckets[line.Bucket] < line.Quantity)
            {
                throw new InvalidOperationException($"Bucket {line.Bucket} cannot supply {line.Quantity} - only {(line.Bucket >= 0 && line.Bucket < buckets.Length ? buckets[line.Bucket] : 0)} available.");
            }

            buckets[line.Bucket] -= line.Quantity;
        }

        return FromBuckets(buckets);
    }

    /// <summary>Gives units back to exactly the buckets they were drawn from.</summary>
    public StockEscrow Release(IReadOnlyList<ReservationLine> lines)
    {
        var buckets = _buckets.ToArray();
        foreach (var line in lines)
        {
            if (line.Bucket < 0 || line.Bucket >= buckets.Length)
            {
                throw new ArgumentOutOfRangeException(nameof(lines));
            }

            buckets[line.Bucket] += line.Quantity;
        }

        return FromBuckets(buckets);
    }

    private static int CircularDistance(int a, int b, int count)
    {
        var direct = Math.Abs(a - b);
        return Math.Min(direct, count - direct);
    }
}
