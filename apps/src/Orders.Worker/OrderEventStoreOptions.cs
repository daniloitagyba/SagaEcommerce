namespace Orders.Worker;

public sealed class OrderEventStoreOptions
{
    public const string SectionName = "OrderEventStore";

    public string BootstrapServers { get; init; } = "localhost:9092";

    public string OrderCreatedTopic { get; init; } = "orders.created.v1";

    public string PaymentResultTopic { get; init; } = "payments.result.v1";

    public string OrderStatusChangedTopic { get; init; } = "orders.status-changed.v1";

    public string ConsumerGroup { get; init; } = "orders-event-store";

    public string ClientId { get; init; } = "orders-event-store";

    public string DeadLetterTopic { get; init; } = "orders.event-store.dlq.v1";
}
