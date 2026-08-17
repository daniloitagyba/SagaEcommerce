using BuildingBlocks;
using Payments.Service.Domain;

namespace Orders.UnitTests;

/// <summary>The authorize/capture state machine, splitting "approved" from "money moved" so a checkout hold and shipment-time funds capture become distinct states; the guards prevent a redelivered capture command from charging twice.</summary>
public class PaymentAuthorizationTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 6, 12, 0, 0, TimeSpan.Zero);
    private static readonly TimeSpan Window = TimeSpan.FromMinutes(30);

    private static Payment Authorize(string method, bool approved = true) =>
        Payment.Authorize(Guid.NewGuid(), "customer-1", 100m, "BRL", method, "01", approved, Now, Window, "correlation-1");

    [Fact]
    public void AnApprovedCardLandsInAuthorizedWithAnExpiringHold()
    {
        var payment = Authorize(PaymentMethods.Card);

        Assert.Equal(PaymentStates.Authorized, payment.State);
        Assert.Equal(Now + Window, payment.AuthorizationExpiresAt);
        Assert.Null(payment.SettledAt);
        Assert.False(PaymentStates.IsSettled(payment.State));
    }

    [Fact]
    public void AnApprovedPixIsCapturedOutrightWithNoHoldToExpire()
    {
        var payment = Authorize(PaymentMethods.Pix);

        Assert.Equal(PaymentStates.Captured, payment.State);
        Assert.Null(payment.AuthorizationExpiresAt);
        Assert.Equal(Now, payment.SettledAt);
        Assert.True(PaymentStates.IsSettled(payment.State));
    }

    [Theory]
    [InlineData(PaymentMethods.Card)]
    [InlineData(PaymentMethods.Pix)]
    public void ADeclinedPaymentIsTerminalRegardlessOfMethod(string method)
    {
        var payment = Authorize(method, approved: false);

        Assert.Equal(PaymentStates.Declined, payment.State);
        Assert.Null(payment.AuthorizationExpiresAt);
        Assert.True(PaymentStates.IsSettled(payment.State));
    }

    [Fact]
    public void CapturingAnAuthorizationMovesTheMoneyOnce()
    {
        var payment = Authorize(PaymentMethods.Card);
        var capturedAt = Now.AddMinutes(5);

        Assert.True(payment.TryCapture(capturedAt));
        Assert.Equal(PaymentStates.Captured, payment.State);
        Assert.Equal(capturedAt, payment.SettledAt);

        Assert.False(payment.TryCapture(Now.AddMinutes(6)));
        Assert.Equal(capturedAt, payment.SettledAt);
    }

    [Fact]
    public void VoidingReleasesAHoldAndCannotBeUndone()
    {
        var payment = Authorize(PaymentMethods.Card);

        Assert.True(payment.TrySettleWithoutCapture(PaymentStates.Voided, "order cancelled", Now.AddMinutes(1)));
        Assert.Equal(PaymentStates.Voided, payment.State);
        Assert.Equal("order cancelled", payment.SettlementReason);

        Assert.False(payment.TrySettleWithoutCapture(PaymentStates.Expired, "sweeper", Now.AddMinutes(2)));
        Assert.False(payment.TryCapture(Now.AddMinutes(3)));
        Assert.Equal(PaymentStates.Voided, payment.State);
    }

    [Fact]
    public void ACapturedPaymentCannotBeVoidedOrExpired()
    {
        var payment = Authorize(PaymentMethods.Card);
        payment.TryCapture(Now.AddMinutes(1));

        Assert.False(payment.TrySettleWithoutCapture(PaymentStates.Voided, "order cancelled", Now.AddMinutes(2)));
        Assert.False(payment.TrySettleWithoutCapture(PaymentStates.Expired, "window elapsed", Now.AddHours(2)));
        Assert.Equal(PaymentStates.Captured, payment.State);
    }

    [Fact]
    public void APixPaymentIsAlreadySettledSoCaptureIsANoOp()
    {
        var payment = Authorize(PaymentMethods.Pix);

        Assert.False(payment.TryCapture(Now.AddMinutes(1)));
        Assert.Equal(PaymentStates.Captured, payment.State);
    }

    [Fact]
    public void OnlyCardsRequireCapture()
    {
        Assert.True(PaymentMethods.RequiresCapture(PaymentMethods.Card));
        Assert.False(PaymentMethods.RequiresCapture(PaymentMethods.Pix));
        Assert.True(PaymentMethods.IsSupported(PaymentMethods.Card));
        Assert.True(PaymentMethods.IsSupported(PaymentMethods.Pix));
        Assert.False(PaymentMethods.IsSupported("Bitcoin"));
        Assert.False(PaymentMethods.IsSupported(null));
    }

    [Fact]
    public void AnApprovedBoletoWaitsForPaymentRatherThanHoldingMoney()
    {
        var payment = Authorize(PaymentMethods.Boleto);

        Assert.Equal(PaymentStates.AwaitingPayment, payment.State);
        Assert.Equal(Now + Window, payment.AuthorizationExpiresAt);
        Assert.Null(payment.SettledAt);
        Assert.False(PaymentStates.IsSettled(payment.State));
    }

    [Fact]
    public void APaidBoletoCapturesLikeACardAndOnlyOnce()
    {
        var payment = Authorize(PaymentMethods.Boleto);

        Assert.True(payment.TryCapture(Now.AddMinutes(5)));
        Assert.Equal(PaymentStates.Captured, payment.State);

        Assert.False(payment.TryCapture(Now.AddMinutes(6)));
    }

    [Fact]
    public void AnUnpaidBoletoExpiresWithoutReleasingAnything()
    {
        var payment = Authorize(PaymentMethods.Boleto);

        Assert.True(payment.TrySettleWithoutCapture(
            PaymentStates.Expired, "payment window elapsed without settlement", Now.AddHours(3)));
        Assert.Equal(PaymentStates.Expired, payment.State);
        Assert.False(payment.TryCapture(Now.AddHours(4)));
    }

    [Fact]
    public void BoletoAndCardBothNeedSettlingButPixDoesNot()
    {
        Assert.True(PaymentMethods.RequiresCapture(PaymentMethods.Card));
        Assert.True(PaymentMethods.RequiresCapture(PaymentMethods.Boleto));
        Assert.False(PaymentMethods.RequiresCapture(PaymentMethods.Pix));

        Assert.Equal(PaymentStates.Authorized, PaymentMethods.PendingStateFor(PaymentMethods.Card));
        Assert.Equal(PaymentStates.AwaitingPayment, PaymentMethods.PendingStateFor(PaymentMethods.Boleto));
    }

    [Fact]
    public void AnAlreadyExpiredPaymentCannotBeVoidedEither()
    {
        var payment = Authorize(PaymentMethods.Card);
        payment.TrySettleWithoutCapture(PaymentStates.Expired, "sweeper", Now.AddMinutes(35));

        Assert.False(payment.TrySettleWithoutCapture(PaymentStates.Voided, "order cancelled", Now.AddMinutes(40)));
        Assert.Equal(PaymentStates.Expired, payment.State);
    }

    [Fact]
    public void CancellingAnAuthorizedCardVoidsTheHold()
    {
        var payment = Authorize(PaymentMethods.Card);

        Assert.True(payment.TryCancel("order cancelled", Now.AddMinutes(1)));
        Assert.Equal(PaymentStates.Voided, payment.State);
        Assert.Equal("order cancelled", payment.SettlementReason);
        Assert.Equal(0m, payment.RefundedAmount);
    }

    [Fact]
    public void CancellingAnAwaitingBoletoVoidsIt()
    {
        var payment = Authorize(PaymentMethods.Boleto);

        Assert.True(payment.TryCancel("order cancelled", Now.AddMinutes(1)));
        Assert.Equal(PaymentStates.Voided, payment.State);
    }

    [Fact]
    public void CancellingACapturedPixRefundsItInFullRatherThanVoidingNothing()
    {
        var payment = Authorize(PaymentMethods.Pix);
        Assert.Equal(PaymentStates.Captured, payment.State);

        Assert.True(payment.TryCancel("order cancelled", Now.AddMinutes(1)));
        Assert.Equal(PaymentStates.Refunded, payment.State);
        Assert.Equal(100m, payment.RefundedAmount);
        Assert.Equal(0m, payment.RefundableAmount);
    }

    [Fact]
    public void CancellingAPartiallyRefundedCapturedPaymentRefundsOnlyWhatRemains()
    {
        var payment = Authorize(PaymentMethods.Pix);
        Assert.True(payment.TryRefund(30m, Now.AddMinutes(1)));
        Assert.Equal(PaymentStates.Captured, payment.State);

        Assert.True(payment.TryCancel("order cancelled", Now.AddMinutes(2)));
        Assert.Equal(PaymentStates.Refunded, payment.State);
        Assert.Equal(100m, payment.RefundedAmount);
    }

    [Theory]
    [InlineData(PaymentMethods.Card)]
    [InlineData(PaymentMethods.Pix)]
    [InlineData(PaymentMethods.Boleto)]
    public void CancellingADeclinedPaymentIsANoOpNotAMismatch(string method)
    {
        var payment = Authorize(method, approved: false);

        Assert.False(payment.TryCancel("order cancelled", Now.AddMinutes(1)));
        Assert.Equal(PaymentStates.Declined, payment.State);
    }

    [Fact]
    public void CancellingAnAlreadyExpiredHoldIsANoOp()
    {
        var payment = Authorize(PaymentMethods.Card);
        payment.TrySettleWithoutCapture(PaymentStates.Expired, "sweeper", Now.AddMinutes(35));

        Assert.False(payment.TryCancel("order cancelled", Now.AddMinutes(40)));
        Assert.Equal(PaymentStates.Expired, payment.State);
    }

    [Fact]
    public void CancellingTwiceVoidsOnceAndTheSecondIsANoOp()
    {
        var payment = Authorize(PaymentMethods.Card);

        Assert.True(payment.TryCancel("order cancelled", Now.AddMinutes(1)));
        Assert.False(payment.TryCancel("order cancelled", Now.AddMinutes(2)));
        Assert.Equal(PaymentStates.Voided, payment.State);
    }

    [Fact]
    public void CancellingAnAlreadyCancelledPixDoesNotDoubleRefund()
    {
        var payment = Authorize(PaymentMethods.Pix);

        Assert.True(payment.TryCancel("order cancelled", Now.AddMinutes(1)));
        Assert.False(payment.TryCancel("order cancelled", Now.AddMinutes(2)));
        Assert.Equal(100m, payment.RefundedAmount);
    }
}
