using BuildingBlocks;
using Orders.Domain;
using Payments.Service.Domain;

namespace Orders.UnitTests;

public sealed class DomainInvariantTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void AnAmountOnlyOrderRequiresAPositiveAmount(decimal amount)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            Order.Create("customer-1", amount, "BRL", DateTimeOffset.UnixEpoch));
    }

    [Fact]
    public void AnOrderCannotDiscountMoreThanItsSubtotal()
    {
        var lines = new[]
        {
            new OrderLineDraft("SKU-1", "Product", "books", 1, 10m, 0m)
        };

        Assert.Throws<ArgumentOutOfRangeException>(() =>
            Order.CreateWithLines(
                "customer-1",
                "BRL",
                DateTimeOffset.UnixEpoch,
                null,
                lines,
                discountTotal: 11m,
                shippingTotal: 0m,
                taxTotal: 0m,
                paymentMethod: PaymentMethods.Pix,
                shippingAddress: null));
    }

    [Fact]
    public void APaymentRejectsAnUnsupportedMethodAtTheDomainBoundary()
    {
        Assert.Throws<ArgumentException>(() => Payment.Authorize(
            Guid.NewGuid(),
            "customer-1",
            100m,
            "BRL",
            "CashUnderTheTable",
            "01",
            approved: true,
            DateTimeOffset.UnixEpoch,
            TimeSpan.FromMinutes(30),
            "correlation-1"));
    }
}
