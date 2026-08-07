namespace Inventory.Service;

public sealed class InventoryKafkaOptions
{
    public const string SectionName = "Kafka";

    public string BootstrapServers { get; init; } = "localhost:9092";

    public string ReservationRequestedTopic { get; init; } = "inventory.reservation-requested.v1";

    public string ReservationRepliedTopic { get; init; } = "inventory.reservation-replied.v1";

    public string RestockRequestedTopic { get; init; } = "inventory.restock-requested.v1";

    public string RestockRepliedTopic { get; init; } = "inventory.restock-replied.v1";

    public string CommitRequestedTopic { get; init; } = "inventory.reservation-commit-requested.v1";

    public string CommitRepliedTopic { get; init; } = "inventory.reservation-commit-replied.v1";

    public string ReleaseRequestedTopic { get; init; } = "inventory.reservation-release-requested.v1";

    public string ReleaseRepliedTopic { get; init; } = "inventory.reservation-release-replied.v1";

    /// <summary>Milestone 73: where WarehouseReplenishmentNeeded lands.</summary>
    public string ReplenishmentNeededTopic { get; init; } = "inventory.replenishment-needed.v1";

    public string DeadLetterTopic { get; init; } = "inventory.reservation.dlq.v1";

    public string ConsumerGroup { get; init; } = "inventory-service";

    public string ClientId { get; init; } = "inventory-service";
}
