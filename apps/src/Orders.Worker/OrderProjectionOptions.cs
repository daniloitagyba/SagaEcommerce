namespace Orders.Worker;

public sealed class OrderProjectionOptions
{
    public const string SectionName = "OrderProjectionKafka";

    public string BootstrapServers { get; init; } = "localhost:9092";

    public string OrderCreatedTopic { get; init; } = "orders.created.v1";

    public string PaymentResultTopic { get; init; } = "payments.result.v1";

    public string OrderStatusChangedTopic { get; init; } = "orders.status-changed.v1";

    public string DeadLetterTopic { get; init; } = "orders.projection.dlq.v1";

    public string ConsumerGroup { get; init; } = "orders-projector";

    public string ClientId { get; init; } = "orders-projector";
}
