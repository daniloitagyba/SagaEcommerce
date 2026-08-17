namespace BuildingBlocks;

/// <summary>The divergence checks the anti-entropy sweep makes, as pure functions of the facts each check compares.</summary>
public static class AntiEntropyChecks
{
    /// <summary>An order committed to charging the shopper must have a Payments record that isn't Declined or missing.</summary>
    public static bool OrderIsMissingAnAccountedPayment(string? paymentState) =>
        paymentState is null or PaymentStates.Declined;

    /// <summary>A backorder row only makes sense while its order is still Backordered.</summary>
    public static bool BackorderBelongsToAnOrderNoLongerWaiting(string orderStatus) =>
        !string.Equals(orderStatus, OrderStatuses.Backordered, StringComparison.Ordinal);

    /// <summary>A committed inventory ledger entry only makes sense while its order isn't Cancelled or missing.</summary>
    public static bool CommittedInventoryBelongsToACancelledOrder(string? orderStatus) =>
        orderStatus is null or OrderStatuses.Cancelled;

    /// <summary>Compares the write model's order status against the read model's projected status.</summary>
    public static bool WriteModelDivergesFromReadModel(string orderStatus, string? summaryStatus) =>
        !string.Equals(orderStatus, summaryStatus, StringComparison.Ordinal);
}
