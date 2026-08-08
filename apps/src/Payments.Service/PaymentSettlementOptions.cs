namespace Payments.Service;

public sealed class PaymentSettlementOptions
{
    public const string SectionName = "PaymentSettlement";

    public string BootstrapServers { get; init; } = "localhost:9092";

    public string CaptureRequestedTopic { get; init; } = "payments.capture-requested.v1";

    public string RefundRequestedTopic { get; init; } = "payments.refund-requested.v1";

    /// <summary>Milestone 81: the order was cancelled - void the hold or refund the capture, whichever the payment's state calls for.</summary>
    public string CancellationRequestedTopic { get; init; } = "payments.cancellation-requested.v1";

    public string SettlementRepliedTopic { get; init; } = "payments.settlement-replied.v1";

    public string DeadLetterTopic { get; init; } = "payments.settlement.dlq.v1";

    public string ConsumerGroup { get; init; } = "payments-service-settlement";

    public string ClientId { get; init; } = "payments-service-settlement";

    /// <summary>How often to look for card authorizations that were never captured.</summary>
    public int ExpirySweepIntervalSeconds { get; init; } = 30;

    public int ExpirySweepBatchSize { get; init; } = 50;
}
