namespace Orders.Worker;

public sealed class SagaOrchestrationOptions
{
    public const string SectionName = "SagaOrchestration";

    public string BootstrapServers { get; init; } = "localhost:9092";

    public string OrderCreatedTopic { get; init; } = "orders.created.v1";

    public string DecisionRequestedTopic { get; init; } = "payments.decision-requested.v1";

    public string DecisionRepliedTopic { get; init; } = "payments.decision-replied.v1";

    public string ReservationRequestedTopic { get; init; } = "inventory.reservation-requested.v1";

    public string ReservationRepliedTopic { get; init; } = "inventory.reservation-replied.v1";

    public string CommitRequestedTopic { get; init; } = "inventory.reservation-commit-requested.v1";

    public string CommitRepliedTopic { get; init; } = "inventory.reservation-commit-replied.v1";

    public string ReleaseRequestedTopic { get; init; } = "inventory.reservation-release-requested.v1";

    public string ReleaseRepliedTopic { get; init; } = "inventory.reservation-release-replied.v1";

    // The Created/Backordered-origin cancellation
    // race - reused wholesale from returns' own restock command
    // and topic, not a new mechanism.
    public string RestockRequestedTopic { get; init; } = "inventory.restock-requested.v1";

    // Not part of the 4-step saga state machine (the saga row
    // is already gone by the time an order ships) - a standalone
    // reconciliation signal, not a step to advance.
    public string SettlementRepliedTopic { get; init; } = "payments.settlement-replied.v1";

    public string RequestConsumerGroup { get; init; } = "orders-saga-orchestrator";

    public string ReplyConsumerGroup { get; init; } = "orders-saga-orchestrator-reply";

    public string ClientId { get; init; } = "orders-saga-orchestrator";

    // Shared by both the request-side (OrderCreatedTopic) and reply-side
    // (the five *Replied topics) consumers - same one-DLQ-per-options-class
    // pattern InventoryKafkaOptions already uses.
    public string DeadLetterTopic { get; init; } = "orders.saga.dlq.v1";

    // Was 5s until Milestone 91 - shorter than this system's own retry
    // budget for a single reservation round trip (saga outbox poll + Kafka
    // produce/retry + the target consumer's own in-process and
    // infrastructure retries; see docs/roadmap-milestones-91-99.md, "the
    // saga timeout is shorter than the system's own retry budget"). A
    // timeout this short cannot tell a slow-but-healthy dependency apart
    // from a genuinely stuck one, so SagaTimeoutSweeper used to fire while
    // a reservation was still legitimately in flight - releasing stock
    // Inventory was about to (or just did) commit for real. 90s is a floor
    // comfortably above the worst-case chain above; Program.cs's
    // ValidateOnStart keeps it from drifting back below that chain if any
    // of the pieces it's built from changes.
    public int TimeoutSeconds { get; init; } = 90;

    public int SweepIntervalMilliseconds { get; init; } = 1_000;

    public int OutboxBatchSize { get; init; } = 50;

    public int OutboxPollIntervalMilliseconds { get; init; } = 250;

    public int OutboxMaximumRetryDelaySeconds { get; init; } = 60;

    /// <summary>
    /// How long a claimed saga-outbox batch's next_attempt_at is pushed
    /// forward while this instance carries it to Kafka outside any open
    /// transaction - the same claim-then-publish-then-mark shape
    /// BuildingBlocks' own OutboxOptions.ClaimWindowSeconds already uses
    /// (see OutboxPublisher.ClaimBatchAsync). Introduced at Milestone 91:
    /// SagaOutboxPublisher previously held these rows' FOR UPDATE locks
    /// (and the open transaction they imply) for as long as the Kafka
    /// publish loop took, which is exactly what OutboxPublisher's own
    /// class comment says never to do - see
    /// docs/roadmap-milestones-91-99.md, "the saga outbox holds Postgres
    /// row locks across the Kafka round trip".
    /// </summary>
    public int OutboxClaimWindowSeconds { get; init; } = 30;

    /// <summary>
    /// After this many failed attempts, a saga command is moved to
    /// saga_outbox_dead_letters instead of retried again - see
    /// SagaOutboxPublisher.MoveToDeadLetterAsync. Without a ceiling, a
    /// command whose payload can never be delivered (a broker rejecting a
    /// dropped topic, a serialization break) retried forever, permanently
    /// inflating the pending backlog and hiding behind the same
    /// OutboxBacklogGrowing alert a real backlog would trip.
    /// </summary>
    public int OutboxMaximumAttempts { get; init; } = 20;
}
