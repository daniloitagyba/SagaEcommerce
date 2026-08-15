namespace BuildingBlocks;

public sealed class OutboxOptions
{
    public const string SectionName = "Outbox";

    public int BatchSize { get; init; } = 5;

    public int PollIntervalMilliseconds { get; init; } = 500;

    public int MaximumRetryDelaySeconds { get; init; } = 60;

    /// <summary>
    /// How long a claimed batch's next_attempt_at is pushed forward while
    /// this instance carries it to Kafka outside any open transaction -
    /// see OutboxPublisher.ClaimBatchAsync. Must comfortably exceed a
    /// slow-broker round trip for a full BatchSize; if this instance dies
    /// before marking the batch published, it simply becomes reclaimable
    /// again once this window elapses; the standard at-least-once outbox
    /// contract, not a new one.
    /// </summary>
    public int ClaimWindowSeconds { get; init; } = 30;

    /// <summary>
    /// How often the pending-backlog gauge's COUNT(*) actually runs, rather
    /// than on every poll tick - the gauge only needs to be roughly
    /// current, and PollIntervalMilliseconds can otherwise run that count
    /// several times a second for no operational benefit.
    /// </summary>
    public int PendingSampleIntervalSeconds { get; init; } = 10;
}
