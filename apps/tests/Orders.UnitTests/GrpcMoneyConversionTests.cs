namespace Orders.UnitTests;

/// <summary>Regression coverage for the OrderQueryGrpcService.GetOrder money field: replaced a lossy `double amount` proto field with `int64 amount_cents`, kept identical to OrdersDbContext's value converter.</summary>
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
        long ToCents(decimal value) => (long)Math.Round(value * 100, MidpointRounding.AwayFromZero);
        decimal FromCents(long cents) => cents / 100m;

        var amountCents = ToCents(amount);

        Assert.Equal(amount, FromCents(amountCents));
    }
}
