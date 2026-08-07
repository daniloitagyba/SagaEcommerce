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
