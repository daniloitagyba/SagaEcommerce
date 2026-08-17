namespace Orders.Worker;

/// <summary>
/// How often, and how much, the anti-entropy sweep looks at per tick.
/// </summary>
public sealed class AntiEntropyOptions
{
    public const string SectionName = "AntiEntropy";

    public int SweepIntervalSeconds { get; init; } = 300;

    /// <summary>
    /// How many candidate rows each check examines per tick.
    /// </summary>
    public int BatchSize { get; init; } = 200;

    public string PaymentsBaseUrl { get; init; } = "http://payments-service:8080";

    public string InventoryBaseUrl { get; init; } = "http://inventory-service:8080";

    /// <summary>
    /// Gets or sets the projection staleness threshold.
    /// </summary>
    public int ProjectionLagThresholdSeconds { get; init; } = 120;

    /// <summary>
    /// Gets or sets the missing-saga threshold.
    /// </summary>
    public int StuckOrderThresholdSeconds { get; init; } = 600;
}
