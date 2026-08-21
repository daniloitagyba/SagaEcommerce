namespace Orders.Worker;

public sealed class AntiEntropyOptions
{
    public const string SectionName = "AntiEntropy";

    public int SweepIntervalSeconds { get; init; } = 300;

    public int BatchSize { get; init; } = 200;

    public string PaymentsBaseUrl { get; init; } = "http://payments-service:8080";

    public string InventoryBaseUrl { get; init; } = "http://inventory-service:8080";

    public int ProjectionLagThresholdSeconds { get; init; } = 120;

    public int StuckOrderThresholdSeconds { get; init; } = 600;
}
