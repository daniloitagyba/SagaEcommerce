using BuildingBlocks;

namespace Orders.UnitTests;

/// <summary>
/// Milestone 88: the sweep's own decision logic, isolated from the
/// database/HTTP calls that gather the two facts it compares - see
/// AntiEntropyChecks for why these are pure functions in the first place.
/// </summary>
public class AntiEntropyChecksTests
{
    [Theory]
    [InlineData(PaymentStates.Authorized)]
    [InlineData(PaymentStates.AwaitingPayment)]
    [InlineData(PaymentStates.Captured)]
    [InlineData(PaymentStates.Voided)]
    [InlineData(PaymentStates.Expired)]
    [InlineData(PaymentStates.Refunded)]
    public void AnOrderWithAnAccountedPaymentStateIsNotADivergence(string paymentState)
    {
        Assert.False(AntiEntropyChecks.OrderIsMissingAnAccountedPayment(paymentState));
    }

    [Fact]
    public void ANullPaymentIsADivergence()
    {
        Assert.True(AntiEntropyChecks.OrderIsMissingAnAccountedPayment(null));
    }

    [Fact]
    public void ADeclinedPaymentOnAConfirmedOrRelatedOrderIsADivergence()
    {
        // A declined payment should never have let the order reach Confirmed in the first place.
        Assert.True(AntiEntropyChecks.OrderIsMissingAnAccountedPayment(PaymentStates.Declined));
    }

    [Fact]
    public void ABackorderOnAnOrderStillBackorderedIsNotADivergence()
    {
        Assert.False(AntiEntropyChecks.BackorderBelongsToAnOrderNoLongerWaiting(OrderStatuses.Backordered));
    }

    [Theory]
    [InlineData(OrderStatuses.Cancelled)]
    [InlineData(OrderStatuses.Confirmed)]
    [InlineData(OrderStatuses.Shipped)]
    [InlineData(OrderStatuses.Delivered)]
    public void ABackorderOnAnyOtherOrderStatusIsADivergence(string orderStatus)
    {
        Assert.True(AntiEntropyChecks.BackorderBelongsToAnOrderNoLongerWaiting(orderStatus));
    }
}
