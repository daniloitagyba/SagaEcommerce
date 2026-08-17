namespace Inventory.Service;

/// <summary>Backorder timeout policy, sourced from configuration.</summary>
public sealed class BackorderOptions
{
    public const string SectionName = "Backorder";

    public int TimeoutSweepIntervalSeconds { get; init; } = 30;

    /// <summary>Bounds distinct SKUs processed per sweep tick, not backorder rows.</summary>
    public int TimeoutSweepBatchSize { get; init; } = 50;

    /// <summary>Minutes an order waits for a restock before the saga gives up.</summary>
    public int TimeoutMinutes { get; init; } = 120;
}
