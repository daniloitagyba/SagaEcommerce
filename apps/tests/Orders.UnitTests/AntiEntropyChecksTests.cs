using BuildingBlocks;

namespace Orders.UnitTests;

/// <summary>The anti-entropy sweep's decision logic, isolated from the database/HTTP calls that gather the facts it compares.</summary>
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

    [Fact]
    public void CommittedInventoryOnACancelledOrderIsADivergence()
    {
        Assert.True(AntiEntropyChecks.CommittedInventoryBelongsToACancelledOrder(OrderStatuses.Cancelled));
    }

    [Fact]
    public void CommittedInventoryWithNoMatchingOrderAtAllIsADivergence()
    {
        Assert.True(AntiEntropyChecks.CommittedInventoryBelongsToACancelledOrder(null));
    }

    [Theory]
    [InlineData(OrderStatuses.Confirmed)]
    [InlineData(OrderStatuses.Picking)]
    [InlineData(OrderStatuses.Shipped)]
    [InlineData(OrderStatuses.Delivered)]
    [InlineData(OrderStatuses.FulfillmentHold)]
    public void CommittedInventoryOnAnyLiveOrderStatusIsNotADivergence(string orderStatus)
    {
        Assert.False(AntiEntropyChecks.CommittedInventoryBelongsToACancelledOrder(orderStatus));
    }

    [Fact]
    public void MatchingWriteAndReadModelStatusesAreNotADivergence()
    {
        Assert.False(AntiEntropyChecks.WriteModelDivergesFromReadModel(OrderStatuses.Shipped, OrderStatuses.Shipped));
    }

    [Fact]
    public void AMismatchedReadModelStatusIsADivergence()
    {
        Assert.True(AntiEntropyChecks.WriteModelDivergesFromReadModel(OrderStatuses.Delivered, OrderStatuses.Shipped));
    }

    [Fact]
    public void AMissingSummaryRowIsADivergence()
    {
        Assert.True(AntiEntropyChecks.WriteModelDivergesFromReadModel(OrderStatuses.Confirmed, null));
    }
}
