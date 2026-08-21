using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Payments.Service.Data;

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

        var historyQuery = payments.Select(payment => new PaymentHistoryEntry(
            payment.Id,
            payment.Amount,
            payment.DecidedAt,
            payment.Approved,
            payment.ShippingPostalPrefix));

        IReadOnlyList<PaymentHistoryEntry> entries;
        DateTimeOffset? firstSeenAt;
        if (string.Equals(dbContext.Database.ProviderName, "Microsoft.EntityFrameworkCore.Sqlite", StringComparison.Ordinal))
        {
            var allEntries = await historyQuery.ToListAsync(cancellationToken);
            firstSeenAt = allEntries.Count == 0 ? null : allEntries.Min(payment => payment.DecidedAt);
            entries = allEntries
                .OrderByDescending(payment => payment.DecidedAt)
                .ThenByDescending(payment => payment.Id)
                .Take(_options.HistoryMaxRows)
                .ToList();
        }
        else
        {
            firstSeenAt = await payments
                .Select(payment => (DateTimeOffset?)payment.DecidedAt)
                .MinAsync(cancellationToken);
            entries = await historyQuery
                .OrderByDescending(payment => payment.DecidedAt)
                .ThenByDescending(payment => payment.Id)
                .Take(_options.HistoryMaxRows)
                .ToListAsync(cancellationToken);
        }

        return new PaymentHistory(firstSeenAt, entries);
    }
}
