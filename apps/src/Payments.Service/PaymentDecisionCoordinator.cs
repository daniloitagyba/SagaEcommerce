using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Payments.Service.Data;
using Payments.Service.Domain;
using Payments.Service.Risk;

namespace Payments.Service;

public sealed record PaymentDecisionInput(
    Guid OrderId,
    string CustomerId,
    decimal Amount,
    string Currency,
    string PaymentMethod,
    string ShippingPostalPrefix,
    string CorrelationId);

public sealed record PaymentDecisionResult(
    Payment Payment,
    RiskAssessment? Assessment,
    bool Created);

public sealed class PaymentDecisionConflictException(Guid orderId)
    : Exception($"Payment decision input conflicts with the primary payment already stored for order '{orderId}'.");

/// <summary>Owns the invariant that an order has exactly one active payment decision, independently of which saga path asks for it.</summary>
public sealed class PaymentDecisionCoordinator(
    PaymentsDbContext dbContext,
    PaymentRiskEvaluator riskEvaluator,
    IOptions<PaymentRiskOptions> riskOptions)
{
    private readonly PaymentRiskOptions _riskOptions = riskOptions.Value;

    public async Task<PaymentDecisionResult> GetOrCreateAsync(
        PaymentDecisionInput input,
        DateTimeOffset decidedAt,
        CancellationToken cancellationToken)
    {
        var lockKey = input.OrderId.ToString("N");
        _ = await dbContext.Database.ExecuteSqlInterpolatedAsync(
            $"SELECT pg_advisory_xact_lock(hashtextextended({lockKey}, 0))",
            cancellationToken);

        var existing = await dbContext.Payments
            .SingleOrDefaultAsync(
                payment => payment.IsPrimary && payment.OrderId == input.OrderId,
                cancellationToken);

        if (existing is not null)
        {
            if (existing.CustomerId != input.CustomerId
                || existing.Amount != input.Amount
                || !string.Equals(existing.Currency, input.Currency, StringComparison.OrdinalIgnoreCase)
                || !string.Equals(existing.Method, input.PaymentMethod, StringComparison.Ordinal)
                || !string.Equals(existing.ShippingPostalPrefix, input.ShippingPostalPrefix, StringComparison.Ordinal))
            {
                throw new PaymentDecisionConflictException(input.OrderId);
            }

            return new PaymentDecisionResult(existing, Assessment: null, Created: false);
        }

        var assessment = await riskEvaluator.EvaluateAsync(
            input.CustomerId,
            input.Amount,
            input.ShippingPostalPrefix,
            decidedAt,
            cancellationToken);

        var payment = Payment.Authorize(
            input.OrderId,
            input.CustomerId,
            input.Amount,
            input.Currency,
            input.PaymentMethod,
            input.ShippingPostalPrefix,
            assessment.Approved,
            decidedAt,
            _riskOptions.SettlementWindowFor(input.PaymentMethod),
            input.CorrelationId);

        dbContext.Payments.Add(payment);
        return new PaymentDecisionResult(payment, assessment, Created: true);
    }
}
