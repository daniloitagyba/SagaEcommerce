namespace BuildingBlocks;

/// <summary>
/// The order's life, as an explicit predecessor table rather than
/// transitions hardcoded pairwise inside <c>OrderStatusStore</c> - that
/// broke down the moment an order could be cancelled from more than one
/// state, with no single place to ask "is this move legal?"
///
/// Expand, not rename: <see cref="Created"/>, <see cref="Confirmed"/> and
/// <see cref="Cancelled"/> keep their exact meanings so existing callers
/// (k6, Pact, the read model, the storefront) keep working.
/// </summary>
public static class OrderStatuses
{
    /// <summary>Accepted and priced; the saga is deciding inventory and payment.</summary>
    public const string Created = "Created";

    /// <summary>Inventory committed and payment authorized. The order is real; fulfilment has not started.</summary>
    public const string Confirmed = "Confirmed";

    /// <summary>
    /// The network couldn't cover the order yet; the saga waits rather than
    /// giving up. No money has moved - payment is decided one step after
    /// reservation - so there's nothing to void, only stock to wait for.
    /// Inventory.Service's backorder release path is what clears this, not
    /// a timer in Orders.Worker.
    /// </summary>
    public const string Backordered = "Backordered";

    /// <summary>The warehouse is assembling it.</summary>
    public const string Picking = "Picking";

    /// <summary>Dispatched - and the point where a card authorization is actually captured.</summary>
    public const string Shipped = "Shipped";

    /// <summary>The happy path's endpoint - not terminal, since a delivered order can still be returned.</summary>
    public const string Delivered = "Delivered";

    /// <summary>Terminal failure. Any card hold still outstanding is voided on the way here.</summary>
    public const string Cancelled = "Cancelled";

    /// <summary>
    /// Everything came back. Reachable only from Delivered - a shipped
    /// order is reversed by a return; an order that never arrived has
    /// nothing to return.
    /// </summary>
    public const string Returned = "Returned";

    /// <summary>
    /// Confirmed, but cannot be fulfilled as-is and needs a human: payment
    /// approved, but the inventory commit reply came back negative. Distinct
    /// from plain "Confirmed" so it doesn't look like a healthy order in
    /// every query, dashboard and read model.
    /// </summary>
    public const string FulfillmentHold = "FulfillmentHold";

    /// <summary>
    /// Legal predecessors per target status - inverted deliberately, since
    /// the compare-and-set asks "may I move <em>to</em> here from wherever
    /// the row is?" and can guard on the whole set in one statement.
    /// </summary>
    private static readonly Dictionary<string, string[]> AllowedPredecessors = new(StringComparer.Ordinal)
    {
        [Backordered] = [Created],
        [Confirmed] = [Created, Backordered],
        [Cancelled] = [Created, Confirmed, Picking, FulfillmentHold, Backordered],
        [FulfillmentHold] = [Confirmed, Picking],
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

    /// <summary>
    /// The states an order may currently be in for a move to
    /// <paramref name="targetStatus"/> to be legal. Empty when the target
    /// is unknown or unreachable (<see cref="Created"/> is only ever set at
    /// construction, never transitioned into).
    /// </summary>
    public static IReadOnlyList<string> PredecessorsOf(string targetStatus) =>
        AllowedPredecessors.TryGetValue(targetStatus, out var allowed) ? allowed : [];

    public static bool CanTransition(string fromStatus, string toStatus) =>
        PredecessorsOf(toStatus).Contains(fromStatus, StringComparer.Ordinal);

    /// <summary>Every status an order can be moved into after creation - the fulfilment API's accepted values.</summary>
    public static IReadOnlyList<string> TransitionableTargets => [.. AllowedPredecessors.Keys];

    /// <summary>
    /// What reaching a status means for money still on hold - centralized
    /// because both Orders.Worker (saga-driven) and Orders.Api
    /// (operator-driven) act on it, and duplicating the policy is what
    /// would let their different mechanisms drift apart.
    /// </summary>
    public static OrderSettlementAction SettlementActionFor(string targetStatus) => targetStatus switch
    {
        // Capture when the goods actually leave - the entire reason for
        // holding an authorization rather than charging at checkout.
        Shipped => OrderSettlementAction.Capture,
        Cancelled => OrderSettlementAction.Void,
        _ => OrderSettlementAction.None
    };
}

public enum OrderSettlementAction
{
    None,
    Capture,
    Void
}
