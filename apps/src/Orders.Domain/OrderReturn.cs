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

    /// <summary>What the customer gets back, summed across the returned lines.</summary>
    public decimal RefundTotal { get; private set; }

    public string Currency { get; private set; } = string.Empty;

    public DateTimeOffset RequestedAt { get; private set; }

    public IReadOnlyList<OrderReturnLine> Lines => _lines;

    internal static OrderReturn Create(
        Guid orderId,
        string customerId,
        string currency,
        string reason,
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
            RequestedAt = requestedAt
        };

        foreach (var line in lines)
        {
            line.AttachTo(orderReturn.Id);
            orderReturn._lines.Add(line);
        }

        orderReturn.RefundTotal = orderReturn._lines.Sum(line => line.RefundAmount);
        return orderReturn;
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
/// Milestone 70: works out what a partial return is worth.
///
/// This is where storing <see cref="OrderLine.LineDiscount"/> per line back
/// in Milestone 66 pays for itself. Refunding a returned unit at its list
/// price would hand back more than was ever charged - a shopper who used a
/// 50% coupon and returns everything would be refunded twice what they
/// paid. The refund has to come out of <see cref="OrderLine.LineTotal"/>,
/// the amount net of that line's prorated discount, and that number only
/// exists because it was computed and stored at checkout rather than
/// re-derived now from rules that may have changed since.
/// </summary>
public static class ReturnRefundCalculator
{
    /// <summary>
    /// Prices a partial return as a slice of the line's charged total.
    ///
    /// Dividing and rounding would break the invariant that matters most:
    /// returning a line one unit at a time must refund exactly what
    /// returning it all at once would. <paramref name="alreadyReturned"/>
    /// moves the starting point along the cumulative curve, so successive
    /// partial returns of the same line consume disjoint slices and can
    /// never together exceed - or fall short of - the line's total.
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

        // The difference between two points on the cumulative curve, rather
        // than summing materialised shares. Two bugs in NodaMoney's Split
        // made that the wrong tool here, both found by the property test
        // below: it throws outright for a share count of 1 (the commonest
        // line there is), and for small amounts spread over many units it
        // emits negative shares - Money(0.06).Split(11) yields ten 0.01s
        // and a -0.04, so returning the first ten units would refund 0.10
        // against a line charged 0.06.
        var upToNow = MoneyAllocation.CumulativeFor(lineTotal, lineQuantity, alreadyReturned + returningNow, currency);
        var upToBefore = MoneyAllocation.CumulativeFor(lineTotal, lineQuantity, alreadyReturned, currency);

        return upToNow - upToBefore;
    }
}
