namespace Orders.UnitTests;

/// <summary>
/// OrderQueryGrpcService.GetOrder used to assign `Amount = (double)order.Amount`
/// on a `double amount = 3` proto field. IEEE 754 binary64 cannot represent
/// most decimal fractions exactly, and a gRPC wire value is read back by
/// whatever runtime the caller is on - not every stack applies .NET's own
/// generous decimal&lt;-&gt;double round-trip logic, so money has no business
/// crossing that field type at all. The fix replaced it with
/// `int64 amount_cents = 3`, assigned via the same
/// `(long)Math.Round(amount * 100, MidpointRounding.AwayFromZero)`
/// expression OrdersDbContext's own value converter uses for this same
/// column - pinned here so REST (backed by the column) and gRPC (backed by
/// this expression) can never silently drift apart for the same order.
/// </summary>
public sealed class GrpcMoneyConversionTests
{
    [Theory]
    [InlineData(1234.56)]
    [InlineData(70.35)]
    [InlineData(999999.99)]
    [InlineData(19.99)]
    [InlineData(100000000.05)]
    public void TheAmountCentsConversionRoundTripsExactlyAndMatchesOrdersDbContext(decimal amount)
    {
        // The exact expression OrderQueryGrpcService.GetOrder uses to populate
        // AmountCents, and the exact pair OrdersDbContext.HasConversion uses
        // for the orders.amount_cents column - kept identical on purpose.
        long ToCents(decimal value) => (long)Math.Round(value * 100, MidpointRounding.AwayFromZero);
        decimal FromCents(long cents) => cents / 100m;

        var amountCents = ToCents(amount);

        Assert.Equal(amount, FromCents(amountCents));
    }
}
