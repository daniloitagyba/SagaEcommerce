namespace BuildingBlocks;

public sealed class KafkaOptions
{
    public const string SectionName = "Kafka";

    public string BootstrapServers { get; init; } = "localhost:9092";

    public string OrderCreatedTopic { get; init; } = "orders.created.v1";

    public string DeadLetterTopic { get; init; } = "orders.created.dlq.v1";

    public string ConsumerGroup { get; init; } = "orders-worker";

    public string ClientId { get; init; } = "orders";
}
