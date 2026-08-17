namespace BuildingBlocks;

/// <summary>The order's life cycle as an explicit predecessor table of legal status transitions.</summary>
public static class OrderStatuses
{
    /// <summary>Accepted and priced; the saga is deciding inventory and payment.</summary>
    public const string Created = "Created";

    /// <summary>Inventory committed and payment authorized. The order is real; fulfilment has not started.</summary>
    public const string Confirmed = "Confirmed";

    /// <summary>Inventory couldn't cover the order yet; the saga waits for stock rather than giving up.</summary>
    public const string Backordered = "Backordered";

    /// <summary>The warehouse is assembling it.</summary>
    public const string Picking = "Picking";

    /// <summary>Dispatched - and the point where a card authorization is actually captured.</summary>
    public const string Shipped = "Shipped";

    /// <summary>The happy path's endpoint - not terminal, since a delivered order can still be returned.</summary>
    public const string Delivered = "Delivered";

    /// <summary>Terminal failure. Any card hold still outstanding is voided on the way here.</summary>
    public const string Cancelled = "Cancelled";

    /// <summary>Everything came back; reachable only from Delivered.</summary>
    public const string Returned = "Returned";

    /// <summary>Confirmed or shipped, but flagged for human review because an inventory commit or payment capture didn't go as expected.</summary>
    public const string FulfillmentHold = "FulfillmentHold";

    /// <summary>Legal predecessors per target status.</summary>
    private static readonly Dictionary<string, string[]> AllowedPredecessors = new(StringComparer.Ordinal)
    {
        [Backordered] = [Created],
        [Confirmed] = [Created, Backordered],
        [Cancelled] = [Created, Confirmed, Picking, FulfillmentHold, Backordered],
        [FulfillmentHold] = [Confirmed, Picking, Shipped],
        [Picking] = [Confirmed, FulfillmentHold],
        [Shipped] = [Picking],
        [Delivered] = [Shipped],
        [Returned] = [Delivered]
    };

    /// <summary>Terminal states: nothing may move out of them, in either direction.</summary>
    public static bool IsTerminal(string status) =>
        status is Cancelled or Returned;

    public static bool IsKnown(string status) =>
        status is Created or Confirmed or Picking or Shipped or Delivered or Cancelled or FulfillmentHold or Returned or Backordered;

    /// <summary>The states an order may currently be in for a move to <paramref name="targetStatus"/> to be legal.</summary>
    public static IReadOnlyList<string> PredecessorsOf(string targetStatus) =>
        AllowedPredecessors.TryGetValue(targetStatus, out var allowed) ? allowed : [];

    public static bool CanTransition(string fromStatus, string toStatus) =>
        PredecessorsOf(toStatus).Contains(fromStatus, StringComparer.Ordinal);

    /// <summary>Every status an order can be moved into after creation, by any means.</summary>
    public static IReadOnlyList<string> TransitionableTargets => [.. AllowedPredecessors.Keys];

    /// <summary>The subset of <see cref="TransitionableTargets"/> an external fulfilment actor may set directly, rather than only the saga or aggregate.</summary>
    public static IReadOnlyList<string> FulfillmentDrivableTargets =>
        [Picking, Shipped, Delivered, FulfillmentHold, Cancelled];

    /// <summary>What reaching a status means for money still on hold.</summary>
    public static OrderSettlementAction SettlementActionFor(string targetStatus) => targetStatus switch
    {
        Shipped => OrderSettlementAction.Capture,
        Cancelled => OrderSettlementAction.Cancel,
        _ => OrderSettlementAction.None
    };
}

public enum OrderSettlementAction
{
    None,
    Capture,
    Cancel
}
