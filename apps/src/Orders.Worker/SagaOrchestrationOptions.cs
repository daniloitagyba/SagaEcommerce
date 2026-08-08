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

    // Milestone 76: not part of the 4-step saga state machine (the saga row
    // is already gone by the time an order ships) - a standalone
    // reconciliation signal, not a step to advance.
    public string SettlementRepliedTopic { get; init; } = "payments.settlement-replied.v1";

    public string RequestConsumerGroup { get; init; } = "orders-saga-orchestrator";

    public string ReplyConsumerGroup { get; init; } = "orders-saga-orchestrator-reply";

    public string ClientId { get; init; } = "orders-saga-orchestrator";

    public int TimeoutSeconds { get; init; } = 5;

    public int SweepIntervalMilliseconds { get; init; } = 1_000;
}
