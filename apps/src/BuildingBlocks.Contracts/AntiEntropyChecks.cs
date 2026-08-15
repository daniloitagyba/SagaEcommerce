namespace BuildingBlocks;

/// <summary>
/// The actual "is this a divergence" decisions the
/// anti-entropy sweep makes, pulled out as pure functions of the two
/// facts each check compares - so they're testable without a database, a
/// Kafka broker, or another service's HTTP endpoint, the same reasoning
/// that already keeps StockAllocator and ReturnRefundCalculator pure.
///
/// Each check answers a question a bug in this codebase has actually
/// asked wrongly before: does this order's status
/// agree with what Payments thinks happened to its money, and does every
/// backorder still belong to an order that is actually waiting.
/// </summary>
public static class AntiEntropyChecks
{
    /// <summary>
    /// An order sitting in Confirmed, Picking, Shipped or FulfillmentHold
    /// has committed to charging (or having charged) the shopper - Payments
    /// must hold a record that isn't Declined. A missing record entirely,
    /// or one still sitting on Declined, means the order believes money
    /// moved (or is on hold) that Payments' own account disagrees with.
    /// </summary>
    public static bool OrderIsMissingAnAccountedPayment(string? paymentState) =>
        paymentState is null or PaymentStates.Declined;

    /// <summary>
    /// A backorder row only makes sense while its order is still
    /// Backordered - waiting on stock. Any other order status (most
    /// concretely, Cancelled - the audit's own bug class) with a
    /// backorder still on file means the wait was never actually cleared.
    /// </summary>
    public static bool BackorderBelongsToAnOrderNoLongerWaiting(string orderStatus) =>
        !string.Equals(orderStatus, OrderStatuses.Backordered, StringComparison.Ordinal);

    /// <summary>
    /// The third check the audit above
    /// wanted from the start and could not build until
    /// Inventory.Service had a ledger surviving settlement to ask it of.
    /// A ledger entry only makes sense while its order is still one the
    /// commit could legitimately belong to - Cancelled is the one status
    /// that specifically means the stock should already have been given
    /// back (cancellation compensation), so a still-open
    /// entry against a Cancelled order is exactly the leak this check
    /// exists to catch. A missing order entirely counts as the same
    /// divergence, the same reasoning BackorderBelongsToAnOrderNoLongerWaiting
    /// already applies to a backorder with no matching order.
    /// </summary>
    public static bool CommittedInventoryBelongsToACancelledOrder(string? orderStatus) =>
        orderStatus is null or OrderStatuses.Cancelled;

    /// <summary>
    /// The fourth check, comparing Orders against itself rather than
    /// another service: orders.status (the write model) against
    /// order_summaries.status (the read model OrderProjectionProcessor
    /// maintains from OrderCreated/PaymentDecided/OrderStatusChanged). Every
    /// status transition now emits OrderStatusChanged specifically so this
    /// projection stays current - a divergence here means that emission, its
    /// outbox delivery, or its consumption silently stopped, exactly the bug
    /// class this system already lived with once (every order reporting
    /// Created forever) before OrderStatusChanged existed.
    /// </summary>
    public static bool WriteModelDivergesFromReadModel(string orderStatus, string? summaryStatus) =>
        !string.Equals(orderStatus, summaryStatus, StringComparison.Ordinal);
}
