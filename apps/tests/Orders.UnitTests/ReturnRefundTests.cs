using CsCheck;
using NodaMoney;
using Orders.Domain;

namespace Orders.UnitTests;

/// <summary>The refund arithmetic and the invariant behind storing per-line discounts: the refund comes out of LineTotal (net of that line's prorated discount), not re-derived from rules that may have changed since.</summary>
public class ReturnRefundTests
{
    private static readonly Currency Brl = Currency.FromCode("BRL");

    private static Money Money(decimal amount) => new(amount, Brl);

    [Fact]
    public void ReturningEveryUnitRefundsExactlyWhatWasCharged()
    {
        var refund = ReturnRefundCalculator.RefundForUnits(Money(100m), 3, alreadyReturned: 0, returningNow: 3, Brl);

        Assert.Equal(Money(100m), refund);
    }

    [Fact]
    public void ReturningOneUnitAtATimeRefundsTheSameTotalAsReturningThemTogether()
    {
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

                var chunk = Math.Max(1, input.quantity / input.splitPoints);
                while (returned < input.quantity)
                {
                    var take = Math.Min(chunk, input.quantity - returned);
                    refunded += ReturnRefundCalculator.RefundForUnits(lineTotal, input.quantity, returned, take, Brl);
                    returned += take;

                    if (refunded > lineTotal)
                    {
                        return false;
                    }
                }

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

    [Fact]
    public void ALineWithTaxRefundsGoodsAndTaxTogether()
    {
        var order = Order.CreateWithLines(
            "customer-1", "BRL", DateTimeOffset.UtcNow, null,
            [new OrderLineDraft("SKU-BOOK-001", "Livro", "books", 2, 100m, LineDiscount: 0m, LineTax: 18m)],
            discountTotal: 0m, shippingTotal: 0m, taxTotal: 18m, paymentMethod: "Pix", shippingAddress: null);
        var line = Assert.Single(order.Lines);

        Assert.Equal(200m, line.LineTotal);
        Assert.Equal(18m, line.LineTax);

        var goodsRefund = ReturnRefundCalculator.RefundForUnits(Money(line.LineTotal), line.Quantity, 0, 2, Brl);
        var taxRefund = ReturnRefundCalculator.RefundForUnits(Money(line.LineTax), line.Quantity, 0, 2, Brl);

        Assert.Equal(Money(200m), goodsRefund);
        Assert.Equal(Money(18m), taxRefund);
        Assert.Equal(Money(218m), goodsRefund + taxRefund);
    }

    [Fact]
    public void APartialReturnRefundsAProportionalShareOfTheLinesTaxToo()
    {
        var taxRefund = ReturnRefundCalculator.RefundForUnits(Money(9m), 3, alreadyReturned: 0, returningNow: 1, Brl);

        Assert.Equal(Money(3m), taxRefund);
    }
}

/// <summary>ShippingRefundPolicy in isolation, pulled out as its own pure function since nothing in Order's public API can advance a fresh order to Delivered for an end-to-end TryReturn test.</summary>
public class ShippingRefundPolicyTests
{
    private static readonly DateTimeOffset CreatedAt = new(2026, 8, 1, 12, 0, 0, TimeSpan.Zero);
    private static readonly TimeSpan SevenDays = TimeSpan.FromDays(7);

    [Fact]
    public void APartialReturnNeverOwesShippingRegardlessOfReason()
    {
        Assert.False(ShippingRefundPolicy.IsOwed(
            orderFullyReturned: false, ReturnReasonCategory.Defect, CreatedAt, CreatedAt.AddDays(1), SevenDays));
    }

    [Fact]
    public void ACompleteDefectReturnAlwaysOwesShippingNoWindow()
    {
        Assert.True(ShippingRefundPolicy.IsOwed(
            orderFullyReturned: true, ReturnReasonCategory.Defect, CreatedAt, CreatedAt.AddDays(400), SevenDays));
    }

    [Fact]
    public void ACompleteRegretReturnInsideTheWindowOwesShipping()
    {
        Assert.True(ShippingRefundPolicy.IsOwed(
            orderFullyReturned: true, ReturnReasonCategory.Regret, CreatedAt, CreatedAt.AddDays(6), SevenDays));
    }

    [Fact]
    public void ACompleteRegretReturnExactlyAtTheWindowBoundaryOwesShipping()
    {
        Assert.True(ShippingRefundPolicy.IsOwed(
            orderFullyReturned: true, ReturnReasonCategory.Regret, CreatedAt, CreatedAt.Add(SevenDays), SevenDays));
    }

    [Fact]
    public void ACompleteRegretReturnPastTheWindowOwesNothing()
    {
        Assert.False(ShippingRefundPolicy.IsOwed(
            orderFullyReturned: true, ReturnReasonCategory.Regret, CreatedAt, CreatedAt.Add(SevenDays).AddSeconds(1), SevenDays));
    }

    [Fact]
    public void ACompleteUnwantedReturnNeverOwesShippingRegardlessOfTiming()
    {
        Assert.False(ShippingRefundPolicy.IsOwed(
            orderFullyReturned: true, ReturnReasonCategory.Unwanted, CreatedAt, CreatedAt.AddMinutes(1), SevenDays));
    }
}
