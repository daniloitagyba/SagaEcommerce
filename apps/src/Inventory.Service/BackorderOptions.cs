namespace Inventory.Service;

/// <summary>
/// Milestone 74: the backorder timeout policy, in configuration for the
/// same reason PaymentRiskOptions's thresholds are - the right window is
/// discovered from how long customers are actually willing to wait, not
/// reasoned out up front.
/// </summary>
public sealed class BackorderOptions
{
    public const string SectionName = "Backorder";

    public int TimeoutSweepIntervalSeconds { get; init; } = 30;

    public int TimeoutSweepBatchSize { get; init; } = 50;

    /// <summary>
    /// How long an order waits for a restock before the saga gives up.
    /// Kept short here for the same reason Payments' authorization window
    /// is 30 minutes rather than days - the sweep has to be observable
    /// inside a lab session.
    /// </summary>
    public int TimeoutMinutes { get; init; } = 120;
}
