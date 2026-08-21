using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Payments.Service.Data;
using Payments.Service.Domain;

namespace Payments.Service.Risk;

public sealed class EfPaymentHistoryReader(
    PaymentsDbContext dbContext,
    IOptions<PaymentRiskOptions> options) : IPaymentHistoryReader
{
    private readonly PaymentRiskOptions _options = options.Value;

    public async Task<PaymentHistory> ReadAsync(string customerId, CancellationToken cancellationToken)
    {
        var payments = dbContext.Payments
            .AsNoTracking()
            .Where(payment => payment.IsPrimary && payment.CustomerId == customerId);

        IReadOnlyList<Payment> topPayments;
        DateTimeOffset? firstSeenAt;
        if (string.Equals(dbContext.Database.ProviderName, "Microsoft.EntityFrameworkCore.Sqlite", StringComparison.Ordinal))
        {
            var allPayments = await payments.ToListAsync(cancellationToken);
            firstSeenAt = allPayments.Count == 0 ? null : allPayments.Min(payment => payment.DecidedAt);
            topPayments = [.. allPayments
                .OrderByDescending(payment => payment.DecidedAt)
                .ThenByDescending(payment => payment.Id)
                .Take(_options.HistoryMaxRows)];
        }
        else
        {
            firstSeenAt = await payments
                .Select(payment => (DateTimeOffset?)payment.DecidedAt)
                .MinAsync(cancellationToken);
            topPayments = await payments
                .OrderByDescending(payment => payment.DecidedAt)
                .ThenByDescending(payment => payment.Id)
                .Take(_options.HistoryMaxRows)
                .ToListAsync(cancellationToken);
        }

        var entries = topPayments
            .Select(payment => new PaymentHistoryEntry(
                payment.Id, payment.Amount, payment.DecidedAt, payment.Approved, payment.ShippingPostalPrefix))
            .ToList();

        return new PaymentHistory(firstSeenAt, entries);
    }
}
