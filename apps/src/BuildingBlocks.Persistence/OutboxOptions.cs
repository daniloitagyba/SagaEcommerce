namespace BuildingBlocks;

public sealed class OutboxOptions
{
    public const string SectionName = "Outbox";

    /// <summary>Batch size for the outbox's concurrent Kafka publish leg.</summary>
    public int BatchSize { get; init; } = 25;

    /// <summary>How many of a claimed batch's Kafka publishes run at once, bounded so one batch can't open unlimited connections.</summary>
    public int MaxConcurrentPublishes { get; init; } = 8;

    public int PollIntervalMilliseconds { get; init; } = 500;

    public int MaximumRetryDelaySeconds { get; init; } = 60;

    /// <summary>How long a claimed batch's next_attempt_at is pushed forward while carried to Kafka outside any open transaction.</summary>
    public int ClaimWindowSeconds { get; init; } = 30;

    /// <summary>How often the pending-backlog gauge's COUNT(*) actually runs, rather than on every poll tick.</summary>
    public int PendingSampleIntervalSeconds { get; init; } = 10;

    /// <summary>After this many failed publish attempts, a row moves to outbox_dead_letters instead of retrying again.</summary>
    public int MaximumAttempts { get; init; } = 20;
}
