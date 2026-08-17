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

    public string RestockRequestedTopic { get; init; } = "inventory.restock-requested.v1";

    public string SettlementRepliedTopic { get; init; } = "payments.settlement-replied.v1";

    public string RequestConsumerGroup { get; init; } = "orders-saga-orchestrator";

    public string ReplyConsumerGroup { get; init; } = "orders-saga-orchestrator-reply";

    public string ClientId { get; init; } = "orders-saga-orchestrator";

    public string DeadLetterTopic { get; init; } = "orders.saga.dlq.v1";

    public int TimeoutSeconds { get; init; } = 90;

    public int SweepIntervalMilliseconds { get; init; } = 1_000;

    public int OutboxBatchSize { get; init; } = 50;

    public int OutboxPollIntervalMilliseconds { get; init; } = 250;

    public int OutboxMaximumRetryDelaySeconds { get; init; } = 60;

    /// <summary>How long a claimed outbox row remains unavailable to other publishers.</summary>
    public int OutboxClaimWindowSeconds { get; init; } = 30;

    /// <summary>Failed commands move to the saga dead-letter table after this many attempts.</summary>
    public int OutboxMaximumAttempts { get; init; } = 20;
}
