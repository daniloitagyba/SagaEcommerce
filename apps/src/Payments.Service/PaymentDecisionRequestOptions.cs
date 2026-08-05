namespace Payments.Service;

public sealed class PaymentDecisionRequestOptions
{
    public const string SectionName = "PaymentDecisionRequest";

    public string BootstrapServers { get; init; } = "localhost:9092";

    public string DecisionRequestedTopic { get; init; } = "payments.decision-requested.v1";

    public string DecisionRepliedTopic { get; init; } = "payments.decision-replied.v1";

    public string DeadLetterTopic { get; init; } = "payments.decision-requested.dlq.v1";

    public string ConsumerGroup { get; init; } = "payments-service-decision-request";

    public string ClientId { get; init; } = "payments-service-decision-request";
}
