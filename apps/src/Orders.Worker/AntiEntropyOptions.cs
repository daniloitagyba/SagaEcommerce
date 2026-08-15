namespace Orders.Worker;

/// <summary>
/// How often, and how much, the anti-entropy sweep looks at
/// per tick. An audit loop, not a hot path - the interval defaults far
/// longer than any other sweeper in this codebase (SagaTimeoutSweeper,
/// BackorderTimeoutSweeper), since it exists to catch a class of bug that
/// should be rare by construction, not to react to one within seconds.
/// </summary>
public sealed class AntiEntropyOptions
{
    public const string SectionName = "AntiEntropy";

    public int SweepIntervalSeconds { get; init; } = 300;

    /// <summary>
    /// How many of the most recently transitioned candidate rows each
    /// check examines per tick. Bounded, not paginated across the whole
    /// table - see the design docs for why a full-table sweep was judged
    /// out of scope for this pass.
    /// </summary>
    public int BatchSize { get; init; } = 200;

    public string PaymentsBaseUrl { get; init; } = "http://payments-service:8080";

    public string InventoryBaseUrl { get; init; } = "http://inventory-service:8080";

    /// <summary>
    /// How long order_summaries' own last projection has to have gone
    /// stale before a status mismatch against orders counts as a
    /// divergence, not just ordinary in-flight outbox/Kafka lag - see
    /// AntiEntropyChecks.WriteModelDivergesFromReadModel. Comfortably
    /// longer than the outbox's own poll interval plus a Kafka round trip.
    /// </summary>
    public int ProjectionLagThresholdSeconds { get; init; } = 120;
}
