namespace Inventory.Service;

/// <summary>Policy for consuming WarehouseReplenishmentNeeded and placing purchase orders.</summary>
public sealed class ReplenishmentOptions
{
    public const string SectionName = "Replenishment";

    /// <summary>How long a purchase order sits Requested before the receiving sweep marks it Received and restocks.</summary>
    public int LeadTimeSeconds { get; init; } = 60;

    public int ReceivingSweepIntervalSeconds { get; init; } = 15;

    public int ReceivingSweepBatchSize { get; init; } = 50;

    /// <summary>Multiplier of ReorderPoint that a purchase order restocks up to.</summary>
    public int TargetMultiplier { get; init; } = 3;
}
