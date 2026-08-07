namespace BuildingBlocks;

/// <summary>
/// Milestone 69: the order's life, as an explicit table rather than
/// literals scattered across four files.
///
/// Until now there were three statuses and every transition was its own
/// hardcoded pair inside <c>OrderStatusStore</c> - <c>"Created" -&gt;
/// "Confirmed"</c>, <c>"Created" -&gt; "Cancelled"</c>. That works for two
/// transitions and stops working the moment an order can be cancelled from
/// more than one state, or reach the same state by more than one route.
/// Worse, it left no place to answer "is this move legal?", so the question
/// was never asked - the CAS's <c>WHERE status = @expected</c> answered a
/// narrower one ("is it in exactly this state?") and the difference only
/// shows up once there is more than one legal predecessor.
///
/// <para>
/// Expand, not rename (the Milestone 66 discipline): <see cref="Created"/>,
/// <see cref="Confirmed"/> and <see cref="Cancelled"/> keep their exact
/// meanings, so the k6 scripts, smoke tests, Pact contracts, the read model
/// and the storefront all keep working. The new states come <em>after</em>
/// Confirmed.
/// </para>
/// </summary>
public static class OrderStatuses
{
    /// <summary>Accepted and priced; the saga is deciding inventory and payment.</summary>
    public const string Created = "Created";

    /// <summary>Inventory committed and payment authorized. The order is real; fulfilment has not started.</summary>
    public const string Confirmed = "Confirmed";

    /// <summary>
    /// Milestone 74: the network could not cover the order right now, and
    /// the saga is waiting rather than giving up. No money has moved yet -
    /// payment is decided one step later than reservation - so there is
    /// nothing to void here, only stock to wait for. A
    /// <see cref="BuildingBlocks.OrderStatuses"/>-external process (the
    /// backorder release path in Inventory.Service) is what moves an order
    /// out of this state, not a timer inside Orders.Worker.
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
    /// Milestone 70: everything came back. Reachable only from Delivered -
    /// a shipped order is reversed by a return, and an order that never
    /// arrived has nothing to return.
    /// </summary>
    public const string Returned = "Returned";

    /// <summary>
    /// Confirmed, but cannot be fulfilled as-is and needs a human.
    ///
    /// This is the home for an outcome the saga has been logging since
    /// Milestone 43 with nowhere to put it: <c>ConfirmedButCommitFailed</c>,
    /// where payment was approved but the inventory commit reply came back
    /// negative. The order was genuinely confirmed - the customer is owed
    /// something - while the stock it depends on was never actually
    /// deducted. Leaving that as plain "Confirmed" made it indistinguishable
    /// from a healthy order in every query, dashboard and read model.
    /// </summary>
    public const string FulfillmentHold = "FulfillmentHold";

    /// <summary>
    /// Which states each status may legally be reached from. Inverted
    /// deliberately: this is the direction the compare-and-set needs, since
    /// it asks "may I move <em>to</em> here, given where the row currently
    /// is?" and can then guard on the whole set in one statement rather
    /// than one round trip per candidate predecessor.
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
    /// What reaching a status means for money still on hold.
    ///
    /// The <em>policy</em> lives here, in one place, because two services
    /// act on it: Orders.Worker (saga-driven Confirmed/Cancelled) and
    /// Orders.Api (operator-driven Picking/Shipped/Delivered/Cancelled).
    /// Their <em>mechanisms</em> differ on purpose - the worker is already
    /// inside an async message handler and produces directly, while the API
    /// is on an HTTP path and writes to the transactional outbox so the
    /// command cannot survive a rolled-back status change. Duplicating the
    /// policy alongside the mechanism is what would let the two drift.
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
