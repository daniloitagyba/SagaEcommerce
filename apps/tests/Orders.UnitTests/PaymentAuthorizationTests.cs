using BuildingBlocks;
using Payments.Service.Domain;

namespace Orders.UnitTests;

/// <summary>
/// Milestone 68: the authorize/capture state machine.
///
/// Until now a payment was one boolean - approved or not - and the money
/// conceptually moved at that instant. That collapses the distinction every
/// card network makes between a hold placed at checkout and funds taken
/// when the goods ship, and it is what made "authorized but not yet
/// charged" an unrepresentable state.
///
/// The guards below are what keep a redelivered capture command from
/// charging twice: the same reasoning as the inbox, applied to a state
/// transition instead of a message id.
/// </summary>
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
        // Pix is an instant transfer: there is no hold to place and nothing
        // to capture later, so modelling it as Authorized would create an
        // authorization no capture command would ever legitimately settle.
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

        // The invariant that matters most: a redelivered capture command
        // must not charge the customer a second time.
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

        // A void that arrives after the void is a no-op...
        Assert.False(payment.TrySettleWithoutCapture(PaymentStates.Expired, "sweeper", Now.AddMinutes(2)));
        // ...and so is a capture: released money cannot be taken back.
        Assert.False(payment.TryCapture(Now.AddMinutes(3)));
        Assert.Equal(PaymentStates.Voided, payment.State);
    }

    [Fact]
    public void ACapturedPaymentCannotBeVoidedOrExpired()
    {
        var payment = Authorize(PaymentMethods.Card);
        payment.TryCapture(Now.AddMinutes(1));

        Assert.False(payment.TrySettleWithoutCapture(PaymentStates.Voided, "order cancelled", Now.AddMinutes(2)));
        // This is the case the expiry sweeper depends on: a payment captured
        // between the sweeper's claim and its update is left alone rather
        // than being expired out from under a charge that already happened.
        Assert.False(payment.TrySettleWithoutCapture(PaymentStates.Expired, "window elapsed", Now.AddHours(2)));
        Assert.Equal(PaymentStates.Captured, payment.State);
    }

    [Fact]
    public void APixPaymentIsAlreadySettledSoCaptureIsANoOp()
    {
        // Orders only sends a capture command for methods that require one,
        // but the domain guard is what makes that an optimisation rather
        // than a correctness requirement.
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
        // Milestone 73. It is deliberately not Authorized: nothing is held.
        // The shopper has a slip and may simply never pay it, which is a
        // different fact about the world than a bank reserving funds, and
        // the state name has to say which one happened.
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

        // A redelivered capture command must be a no-op, not a second charge.
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
}
