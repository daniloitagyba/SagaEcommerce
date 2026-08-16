namespace BuildingBlocks;

public sealed class OutboxOptions
{
    public const string SectionName = "Outbox";

    /// <summary>
    /// Raised from 5 to 25 at Milestone 91, now that the batch's Kafka
    /// publish leg runs concurrently (see MaxConcurrentPublishes and
    /// OutboxPublisher.PublishAllAsync) instead of one message at a time -
    /// a low BatchSize was tuned for that old serial loop, where a bigger
    /// batch just meant a longer sequential wait; it no longer needs to be.
    /// See docs/roadmap-milestones-91-99.md, "the shared outbox publishes
    /// 5 rows at a time, one await each".
    /// </summary>
    public int BatchSize { get; init; } = 25;

    /// <summary>
    /// How many of a claimed batch's Kafka publishes run at once - see
    /// OutboxPublisher.PublishAllAsync. Bounded, not unlimited, so one
    /// oversized batch can't open BatchSize simultaneous connections/requests
    /// against the same Kafka producer.
    /// </summary>
    public int MaxConcurrentPublishes { get; init; } = 8;

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

    /// <summary>
    /// After this many failed publish attempts, a row moves to
    /// outbox_dead_letters instead of retrying again - see
    /// OutboxPublisher.DeadLetterAsync. Before Milestone 91 there was no
    /// ceiling: a row whose payload could never be delivered (an event
    /// type the dispatcher stopped recognizing, a message too large for
    /// the broker) retried every MaximumRetryDelaySeconds forever,
    /// permanently inflating the pending-backlog gauge and staying exempt
    /// from RetentionSweeper, which only ever deletes processed rows - see
    /// docs/roadmap-milestones-91-99.md, "neither outbox can ever give up".
    /// </summary>
    public int MaximumAttempts { get; init; } = 20;
}
