using Payments.Service.Risk;

namespace Payments.UnitTests;

public sealed class PaymentRiskPolicyTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 17, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void FirstPurchaseAndHighValueSignalsAreEvaluatedWithoutPersistence()
    {
        var assessment = new PaymentRiskPolicy(new PaymentRiskOptions())
            .Evaluate("customer-1", 5_000m, "01", Now, PaymentHistory.Empty);

        Assert.False(assessment.Approved);
        Assert.Equal(70, assessment.Score);
        Assert.Contains(assessment.Signals, signal => signal.Code == "HIGH_VALUE");
        Assert.Contains(assessment.Signals, signal => signal.Code == "FIRST_PURCHASE");
    }

    [Fact]
    public void KnownAddressAndNormalAmountProduceNoRiskSignals()
    {
        var history = new PaymentHistory(
            Now.AddDays(-30),
            [new PaymentHistoryEntry(Guid.NewGuid(), 100m, Now.AddDays(-1), true, "01")]);

        var assessment = new PaymentRiskPolicy(new PaymentRiskOptions())
            .Evaluate("customer-1", 110m, "01", Now, history);

        Assert.True(assessment.Approved);
        Assert.Empty(assessment.Signals);
    }

    [Fact]
    public void NewAddressAndHighVelocityAreCombinedDeterministically()
    {
        var history = new PaymentHistory(
            Now.AddMinutes(-10),
            [
                new PaymentHistoryEntry(Guid.NewGuid(), 40m, Now.AddMinutes(-1), true, "01"),
                new PaymentHistoryEntry(Guid.NewGuid(), 40m, Now.AddMinutes(-2), true, "01"),
                new PaymentHistoryEntry(Guid.NewGuid(), 40m, Now.AddMinutes(-3), true, "01")
            ]);

        var assessment = new PaymentRiskPolicy(new PaymentRiskOptions())
            .Evaluate("customer-1", 40m, "66", Now, history);

        Assert.False(assessment.Approved);
        Assert.Equal(80, assessment.Score);
        Assert.Contains(assessment.Signals, signal => signal.Code == "NEW_ACCOUNT");
        Assert.Contains(assessment.Signals, signal => signal.Code == "VELOCITY");
        Assert.Contains(assessment.Signals, signal => signal.Code == "ADDRESS_MISMATCH");
    }
}
