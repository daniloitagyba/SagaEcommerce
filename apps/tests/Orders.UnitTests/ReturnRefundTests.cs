using CsCheck;
using NodaMoney;
using Orders.Domain;

namespace Orders.UnitTests;

/// <summary>
/// Milestone 70: the refund arithmetic, and the invariant that makes
/// storing per-line discounts in Milestone 66 worth it - refunding at list
/// price would hand back more than was ever charged. The refund comes out
/// of LineTotal, net of that line's prorated discount, computed at
/// checkout rather than re-derived from rules that may have changed since.
/// </summary>
public class ReturnRefundTests
{
    private static readonly Currency Brl = Currency.FromCode("BRL");

    private static Money Money(decimal amount) => new(amount, Brl);

    [Fact]
    public void ReturningEveryUnitRefundsExactlyWhatWasCharged()
    {
        // 3 units charged 100,00 in total after discount. The naive
        // 100.00 / 3 = 33.33 refunds 99,99 and keeps a centavo.
        var refund = ReturnRefundCalculator.RefundForUnits(Money(100m), 3, alreadyReturned: 0, returningNow: 3, Brl);

        Assert.Equal(Money(100m), refund);
    }

    [Fact]
    public void ReturningOneUnitAtATimeRefundsTheSameTotalAsReturningThemTogether()
    {
        // Three returns in three parcels must refund exactly what one parcel of three would have.
        var lineTotal = Money(100m);
        var piecemeal = Money(0m);

        for (var returned = 0; returned < 3; returned++)
        {
            piecemeal += ReturnRefundCalculator.RefundForUnits(lineTotal, 3, returned, 1, Brl);
        }

        Assert.Equal(lineTotal, piecemeal);
        Assert.Equal(
            ReturnRefundCalculator.RefundForUnits(lineTotal, 3, 0, 3, Brl),
            piecemeal);
    }

    [Fact]
    public void ADiscountedLineRefundsTheDiscountedAmountNotTheListPrice()
    {
        // 2 units at 89,90 = 179,80 list, less a 26,61 coupon share = 153,19
        // charged. Built through the real checkout constructor, not the
        // internal factory, so these are numbers a real order would carry.
        var order = Order.CreateWithLines(
            "customer-1", "BRL", DateTimeOffset.UtcNow, "SAVE10",
            [new OrderLineDraft("SKU-BOOK-001", "Livro", "books", 2, 89.90m, 26.61m)],
            discountTotal: 26.61m, shippingTotal: 0m, taxTotal: 0m, paymentMethod: "Pix", shippingAddress: null);
        var line = Assert.Single(order.Lines);

        Assert.Equal(179.80m, line.LineSubtotal);
        Assert.Equal(153.19m, line.LineTotal);

        var refund = ReturnRefundCalculator.RefundForUnits(Money(line.LineTotal), line.Quantity, 0, 2, Brl);

        Assert.Equal(Money(153.19m), refund);
        Assert.NotEqual(Money(line.LineSubtotal), refund);
    }

    [Fact]
    public void ReturningNothingRefundsNothing()
    {
        Assert.Equal(Money(0m), ReturnRefundCalculator.RefundForUnits(Money(100m), 3, 0, 0, Brl));
        Assert.Equal(Money(0m), ReturnRefundCalculator.RefundForUnits(Money(100m), 3, 0, -1, Brl));
    }

    [Fact]
    public void PartialReturnsOfALineCanNeverTogetherExceedWhatWasCharged()
    {
        // No sequence of partial returns may refund more than the line was charged, and returning everything must refund exactly it.
        var gen =
            from cents in Gen.Int[1, 500_00]
            from quantity in Gen.Int[1, 12]
            from splitPoints in Gen.Int[1, 4]
            select (cents, quantity, splitPoints);

        gen.Sample(
            input =>
            {
                var lineTotal = Money(input.cents / 100m);
                var refunded = Money(0m);
                var returned = 0;

                // Return the line in arbitrarily sized chunks until it is
                // exhausted.
                var chunk = Math.Max(1, input.quantity / input.splitPoints);
                while (returned < input.quantity)
                {
                    var take = Math.Min(chunk, input.quantity - returned);
                    refunded += ReturnRefundCalculator.RefundForUnits(lineTotal, input.quantity, returned, take, Brl);
                    returned += take;

                    // Never overshoot, at any intermediate point.
                    if (refunded > lineTotal)
                    {
                        return false;
                    }
                }

                // And returning everything refunds exactly the line total.
                return refunded == lineTotal;
            },
            iter: 10_000);
    }

    [Fact]
    public void EveryRefundShareIsAWholeCentavo()
    {
        Gen.Select(Gen.Int[1, 500_00], Gen.Int[1, 12], Gen.Int[1, 12]).Sample(
            input =>
            {
                var (cents, quantity, requested) = input;
                var take = Math.Min(requested, quantity);
                var refund = ReturnRefundCalculator.RefundForUnits(Money(cents / 100m), quantity, 0, take, Brl);
                return decimal.Round(refund.Amount, 2) == refund.Amount && refund.Amount >= 0m;
            },
            iter: 10_000);
    }
}
