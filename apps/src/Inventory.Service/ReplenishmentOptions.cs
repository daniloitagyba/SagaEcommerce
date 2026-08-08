namespace Inventory.Service;

/// <summary>
/// Milestone 89: closes the loop Milestone 73 left open -
/// WarehouseReplenishmentNeeded finally has a consumer. LeadTimeSeconds is
/// this lab's usual compression of a real supplier's lead time (days) into
/// something a lab session can actually watch happen, the same trade
/// Milestone 73 already made for a boleto's due date.
/// </summary>
public sealed class ReplenishmentOptions
{
    public const string SectionName = "Replenishment";

    /// <summary>How long a purchase order sits Requested before the receiving sweep marks it Received and restocks.</summary>
    public int LeadTimeSeconds { get; init; } = 60;

    public int ReceivingSweepIntervalSeconds { get; init; } = 15;

    public int ReceivingSweepBatchSize { get; init; } = 50;

    /// <summary>
    /// A purchase order requests enough to bring available stock up to
    /// ReorderPoint times this multiplier, not merely back to the reorder
    /// point itself - restocking to exactly the threshold that just
    /// triggered the alert would cross it again on the very next sale.
    /// </summary>
    public int TargetMultiplier { get; init; } = 3;
}
