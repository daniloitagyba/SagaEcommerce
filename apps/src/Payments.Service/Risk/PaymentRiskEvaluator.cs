namespace Payments.Service.Risk;

public sealed record RiskSignal(string Code, string Description, int Score);

public sealed record RiskAssessment(int Score, bool Approved, IReadOnlyList<RiskSignal> Signals)
{
    public string ReasonSummary => Signals.Count == 0
        ? "no risk signals"
        : string.Join("; ", Signals.Select(signal => $"{signal.Code}(+{signal.Score})"));
}

/// <summary>Scores a payment decision from signals dependent on the customer's history and buying pace.</summary>
public sealed class PaymentRiskEvaluator(IPaymentHistoryReader historyReader, PaymentRiskPolicy policy)
{
    public async Task<RiskAssessment> EvaluateAsync(
        string customerId,
        decimal amount,
        DateTimeOffset now,
        CancellationToken cancellationToken) =>
        await EvaluateAsync(customerId, amount, string.Empty, now, cancellationToken);

    public async Task<RiskAssessment> EvaluateAsync(
        string customerId,
        decimal amount,
        string shippingPostalPrefix,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(customerId))
        {
            return policy.Evaluate(customerId, amount, shippingPostalPrefix, now, PaymentHistory.Empty);
        }

        var history = await historyReader.ReadAsync(customerId, cancellationToken);
        return policy.Evaluate(customerId, amount, shippingPostalPrefix, now, history);
    }
}
