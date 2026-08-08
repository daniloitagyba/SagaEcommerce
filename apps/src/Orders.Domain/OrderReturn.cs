using NodaMoney;
using Orders.Domain.Pricing;

namespace Orders.Domain;

/// <summary>
/// Milestone 70: a customer sending some of an order back.
///
/// Partial by nature - "two of the three shirts, keep the book" is the
/// ordinary case, not an edge case - which is what makes the refund
/// arithmetic the interesting part rather than the paperwork.
/// </summary>
public sealed class OrderReturn
{
    private readonly List<OrderReturnLine> _lines = [];

    private OrderReturn()
    {
    }

    public Guid Id { get; private set; }

    public Guid OrderId { get; private set; }

    public string CustomerId { get; private set; } = string.Empty;

    public string Reason { get; private set; } = string.Empty;

    /// <summary>
    /// Milestone 82: what refund policy this return is under - separate
    /// from the free-text <see cref="Reason"/>, which is a description for
    /// the record, not something arithmetic can branch on.
    /// </summary>
    public ReturnReasonCategory ReasonCategory { get; private set; }

    /// <summary>
    /// Milestone 82: this order's outbound shipping, refunded only on a
    /// complete return under a policy that owes it (see
    /// <see cref="Order.TryReturn"/>) - shipping is charged once, at the
    /// order level, never per line, so there is nothing to prorate here.
    /// </summary>
    public decimal ShippingRefund { get; private set; }

    /// <summary>What the customer gets back: every returned line's goods and tax, plus ShippingRefund when owed.</summary>
    public decimal RefundTotal { get; private set; }

    public string Currency { get; private set; } = string.Empty;

    public DateTimeOffset RequestedAt { get; private set; }

    public IReadOnlyList<OrderReturnLine> Lines => _lines;

    internal static OrderReturn Create(
        Guid orderId,
        string customerId,
        string currency,
        string reason,
        ReturnReasonCategory reasonCategory,
        decimal shippingRefund,
        DateTimeOffset requestedAt,
        IReadOnlyList<OrderReturnLine> lines)
    {
        var orderReturn = new OrderReturn
        {
            Id = Guid.NewGuid(),
            OrderId = orderId,
            CustomerId = customerId,
            Currency = currency,
            Reason = reason,
            ReasonCategory = reasonCategory,
            ShippingRefund = shippingRefund,
            RequestedAt = requestedAt
        };

        foreach (var line in lines)
        {
            line.AttachTo(orderReturn.Id);
            orderReturn._lines.Add(line);
        }

        orderReturn.RefundTotal = orderReturn._lines.Sum(line => line.RefundAmount) + shippingRefund;
        return orderReturn;
    }
}

/// <summary>
/// Milestone 82: the policy that decides whether outbound shipping comes
/// back on a complete return, alongside the goods and their tax. Not a
/// free-text description - see <see cref="OrderReturn.Reason"/> for that.
/// </summary>
public enum ReturnReasonCategory
{
    /// <summary>The item was faulty or wrong. Shipping refunds in full on a complete return, no window.</summary>
    Defect,

    /// <summary>The shopper changed their mind - CDC art. 49's arrependimento. Shipping refunds in full on a complete return, but only inside the regret window.</summary>
    Regret,

    /// <summary>A discretionary return outside any legal entitlement. Goods and tax come back; shipping does not.</summary>
    Unwanted
}

/// <summary>
/// Milestone 82: whether outbound shipping is owed on a return, pulled out
/// of <see cref="Order.TryReturn"/> as its own pure function - same
/// reasoning that keeps <see cref="ReturnRefundCalculator"/> a standalone,
/// directly testable piece of arithmetic rather than inline logic no test
/// can reach without first getting an <see cref="Order"/> all the way to
/// <c>Delivered</c>, which nothing in this aggregate's public API can do.
/// </summary>
public static class ShippingRefundPolicy
{
    public static bool IsOwed(
        bool orderFullyReturned,
        ReturnReasonCategory reasonCategory,
        DateTimeOffset orderCreatedAt,
        DateTimeOffset requestedAt,
        TimeSpan regretWindow)
    {
        if (!orderFullyReturned)
        {
            // A partial return keeps the order open for more shipments to
            // come back on - shipping was for the whole parcel, not this slice of it.
            return false;
        }

        return reasonCategory switch
        {
            ReturnReasonCategory.Defect => true,
            ReturnReasonCategory.Regret => requestedAt - orderCreatedAt <= regretWindow,
            _ => false
        };
    }
}

public sealed class OrderReturnLine
{
    private OrderReturnLine()
    {
    }

    public Guid Id { get; private set; }

    public Guid ReturnId { get; private set; }

    public string Sku { get; private set; } = string.Empty;

    public int Quantity { get; private set; }

    /// <summary>
    /// This line's share of what was actually charged - net of the discount
    /// it received, never the list price.
    /// </summary>
    public decimal RefundAmount { get; private set; }

    internal static OrderReturnLine Create(string sku, int quantity, decimal refundAmount) =>
        new() { Id = Guid.NewGuid(), Sku = sku, Quantity = quantity, RefundAmount = refundAmount };

    internal void AttachTo(Guid returnId) => ReturnId = returnId;
}

public enum ReturnRejectionReason
{
    None,
    OrderNotDelivered,
    UnknownSku,
    QuantityNotPositive,
    ExceedsPurchasedQuantity,
    NothingToReturn
}

/// <summary>
/// Milestone 70: works out what a partial return is worth, out of
/// <see cref="OrderLine.LineTotal"/> - net of the line's prorated discount,
/// stored at checkout - rather than list price, or a shopper who used a
/// 50% coupon and returns everything would be refunded twice what they paid.
/// </summary>
public static class ReturnRefundCalculator
{
    /// <summary>
    /// Prices a partial return as a slice of the line's charged total, so
    /// returning a line one unit at a time refunds exactly what returning
    /// it all at once would. <paramref name="alreadyReturned"/> moves the
    /// starting point along the cumulative curve, so successive partial
    /// returns consume disjoint slices that never exceed the line's total.
    /// </summary>
    public static Money RefundForUnits(
        Money lineTotal,
        int lineQuantity,
        int alreadyReturned,
        int returningNow,
        Currency currency)
    {
        if (returningNow <= 0 || lineQuantity <= 0)
        {
            return new Money(0m, currency);
        }

        // Difference between two points on the cumulative curve, not summed
        // materialised shares - NodaMoney's Split throws for a share count
        // of 1 and can emit negative shares for small amounts (see MoneyAllocation).
        var upToNow = MoneyAllocation.CumulativeFor(lineTotal, lineQuantity, alreadyReturned + returningNow, currency);
        var upToBefore = MoneyAllocation.CumulativeFor(lineTotal, lineQuantity, alreadyReturned, currency);

        return upToNow - upToBefore;
    }
}
