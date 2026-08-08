namespace BuildingBlocks;

/// <summary>
/// Milestone 88: the actual "is this a divergence" decisions the
/// anti-entropy sweep makes, pulled out as pure functions of the two
/// facts each check compares - so they're testable without a database, a
/// Kafka broker, or another service's HTTP endpoint, the same reasoning
/// that already keeps StockAllocator and ReturnRefundCalculator pure.
///
/// Each check answers a question a bug in this codebase has actually
/// asked wrongly before (Milestone 81's audit): does this order's status
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
    /// concretely, Cancelled - Milestone 81's own bug class) with a
    /// backorder still on file means the wait was never actually cleared.
    /// </summary>
    public static bool BackorderBelongsToAnOrderNoLongerWaiting(string orderStatus) =>
        !string.Equals(orderStatus, OrderStatuses.Backordered, StringComparison.Ordinal);
}
