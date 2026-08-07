using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Payments.Service.Data;
using Payments.Service.Domain;
using Payments.Service.Risk;

namespace Orders.UnitTests;

/// <summary>
/// Milestone 66: the risk signals, against a real (SQLite in-memory)
/// PaymentsDbContext rather than a mocked repository - the rules are
/// mostly queries over payment history, so mocking the query away would
/// leave almost nothing under test.
/// </summary>
public sealed class PaymentRiskEvaluatorTests : IAsyncLifetime, IDisposable
{
    private readonly Microsoft.Data.Sqlite.SqliteConnection _connection = new("DataSource=:memory:");
    private PaymentsDbContext _dbContext = null!;

    // xUnit drives cleanup through DisposeAsync; IDisposable is here to
    // satisfy CA1001, which only recognises the synchronous pattern.
    public void Dispose()
    {
        _dbContext?.Dispose();
        _connection.Dispose();
    }

    public async Task InitializeAsync()
    {
        await _connection.OpenAsync();
        _dbContext = new PaymentsDbContext(
            new DbContextOptionsBuilder<PaymentsDbContext>().UseSqlite(_connection).Options);
        await _dbContext.Database.EnsureCreatedAsync();
    }

    public async Task DisposeAsync()
    {
        await _dbContext.DisposeAsync();
        await _connection.DisposeAsync();
    }

    private PaymentRiskEvaluator Evaluator(PaymentRiskOptions? options = null) =>
        new(_dbContext, Options.Create(options ?? new PaymentRiskOptions()));

    private async Task SeedAsync(string customerId, decimal amount, DateTimeOffset decidedAt, bool approved = true, string postalPrefix = "01")
    {
        _dbContext.Payments.Add(Payment.Authorize(
            Guid.NewGuid(), customerId, amount, "BRL", BuildingBlocks.PaymentMethods.Pix, postalPrefix,
            approved, decidedAt, TimeSpan.FromMinutes(30), "correlation"));
        await _dbContext.SaveChangesAsync();
    }

    [Fact]
    public async Task AFirstPurchaseIsFlaggedButNotDeclinedOnItsOwn()
    {
        var assessment = await Evaluator().EvaluateAsync("new-customer", 50m, DateTimeOffset.UtcNow, CancellationToken.None);

        Assert.True(assessment.Approved);
        Assert.Equal(20, assessment.Score);
        Assert.Contains(assessment.Signals, signal => signal.Code == "FIRST_PURCHASE");
    }

    [Fact]
    public async Task ALargeFirstPurchaseCompoundsIntoADecline()
    {
        // Neither signal declines alone (50 and 20 are under 60); together they cross the threshold - the whole reason it's scored, not boolean.
        var assessment = await Evaluator().EvaluateAsync("new-customer", 5_000m, DateTimeOffset.UtcNow, CancellationToken.None);

        Assert.False(assessment.Approved);
        Assert.Equal(70, assessment.Score);
        Assert.Contains(assessment.Signals, signal => signal.Code == "HIGH_VALUE");
        Assert.Contains(assessment.Signals, signal => signal.Code == "FIRST_PURCHASE");
    }

    [Fact]
    public async Task AnEstablishedCustomerBuyingNormallyIsApprovedCleanly()
    {
        var now = DateTimeOffset.UtcNow;
        await SeedAsync("regular", 100m, now.AddDays(-30));
        await SeedAsync("regular", 120m, now.AddDays(-10));

        var assessment = await Evaluator().EvaluateAsync("regular", 110m, now, CancellationToken.None);

        Assert.True(assessment.Approved);
        Assert.Equal(0, assessment.Score);
        Assert.Empty(assessment.Signals);
        Assert.Equal("no risk signals", assessment.ReasonSummary);
    }

    [Fact]
    public async Task RapidRepeatPurchasesTripTheVelocitySignal()
    {
        // The old account isolates VELOCITY - without it, NEW_ACCOUNT(25) would also compound in (pinned separately below).
        var now = DateTimeOffset.UtcNow;
        await SeedAsync("fast", 40m, now.AddDays(-90));
        await SeedAsync("fast", 40m, now.AddMinutes(-1));
        await SeedAsync("fast", 40m, now.AddMinutes(-2));
        await SeedAsync("fast", 40m, now.AddMinutes(-3));

        var assessment = await Evaluator().EvaluateAsync("fast", 40m, "01", now, CancellationToken.None);

        Assert.Contains(assessment.Signals, signal => signal.Code == "VELOCITY");
        Assert.Equal(35, assessment.Score);
        Assert.True(assessment.Approved);
    }

    [Fact]
    public async Task OlderPurchasesDoNotCountTowardsVelocity()
    {
        var now = DateTimeOffset.UtcNow;
        await SeedAsync("paced", 40m, now.AddHours(-1));
        await SeedAsync("paced", 40m, now.AddHours(-2));
        await SeedAsync("paced", 40m, now.AddHours(-3));

        var assessment = await Evaluator().EvaluateAsync("paced", 40m, now, CancellationToken.None);

        Assert.DoesNotContain(assessment.Signals, signal => signal.Code == "VELOCITY");
    }

    [Fact]
    public async Task AnOrderFarAboveTheCustomersNormIsFlagged()
    {
        var now = DateTimeOffset.UtcNow;
        await SeedAsync("modest", 20m, now.AddDays(-20));
        await SeedAsync("modest", 30m, now.AddDays(-10));

        // Average is 25; 200 is 8x that (past the 5x multiplier) but well under HIGH_VALUE, so only the relative signal fires.
        var assessment = await Evaluator().EvaluateAsync("modest", 200m, now, CancellationToken.None);

        Assert.Contains(assessment.Signals, signal => signal.Code == "ATYPICAL_AMOUNT");
        Assert.DoesNotContain(assessment.Signals, signal => signal.Code == "HIGH_VALUE");
        Assert.Equal(30, assessment.Score);
    }

    [Fact]
    public async Task DeclinedHistoryDoesNotEstablishACustomersNormalSpend()
    {
        var now = DateTimeOffset.UtcNow;
        // A declined 10,000 attempt must not raise this customer's "average" - or a fraudster could normalise their own baseline by failing loudly first.
        await SeedAsync("probing", 10_000m, now.AddMinutes(-30), approved: false);
        await SeedAsync("probing", 20m, now.AddDays(-5));

        var assessment = await Evaluator().EvaluateAsync("probing", 500m, now, CancellationToken.None);

        Assert.Contains(assessment.Signals, signal => signal.Code == "ATYPICAL_AMOUNT");
    }

    [Fact]
    public async Task AnUnknownCustomerIsNotScoredAsAFirstPurchase()
    {
        // A decision request that lost its CustomerId must not be scored as a brand-new customer.
        var assessment = await Evaluator().EvaluateAsync("", 50m, DateTimeOffset.UtcNow, CancellationToken.None);

        Assert.True(assessment.Approved);
        Assert.Empty(assessment.Signals);
    }

    [Fact]
    public async Task AnAccountThatAppearedMinutesAgoScoresAsNewOnItsSecondOrder()
    {
        // FIRST_PURCHASE has already stopped firing by the second order, yet a minutes-old account isn't a returning customer either.
        var now = DateTimeOffset.UtcNow;
        await SeedAsync("fresh", 50m, now.AddMinutes(-10));

        var assessment = await Evaluator().EvaluateAsync("fresh", 50m, "01", now, CancellationToken.None);

        Assert.Contains(assessment.Signals, signal => signal.Code == "NEW_ACCOUNT");
        Assert.DoesNotContain(assessment.Signals, signal => signal.Code == "FIRST_PURCHASE");
    }

    [Fact]
    public async Task AnEstablishedAccountIsNotScoredAsNew()
    {
        var now = DateTimeOffset.UtcNow;
        await SeedAsync("settled", 50m, now.AddDays(-30));

        var assessment = await Evaluator().EvaluateAsync("settled", 50m, "01", now, CancellationToken.None);

        Assert.DoesNotContain(assessment.Signals, signal => signal.Code == "NEW_ACCOUNT");
    }

    [Fact]
    public async Task ShippingSomewhereThisCustomerNeverHasIsFlagged()
    {
        var now = DateTimeOffset.UtcNow;
        await SeedAsync("mover", 50m, now.AddDays(-40), postalPrefix: "01");
        await SeedAsync("mover", 60m, now.AddDays(-20), postalPrefix: "01");

        var assessment = await Evaluator().EvaluateAsync("mover", 50m, "66", now, CancellationToken.None);

        Assert.Contains(assessment.Signals, signal => signal.Code == "ADDRESS_MISMATCH");

        // Ordinary on its own - people move and send gifts.
        Assert.True(assessment.Approved);
    }

    [Fact]
    public async Task ShippingWhereThisCustomerAlwaysShipsIsNotFlagged()
    {
        var now = DateTimeOffset.UtcNow;
        await SeedAsync("regular", 50m, now.AddDays(-40), postalPrefix: "01");

        var assessment = await Evaluator().EvaluateAsync("regular", 50m, "01", now, CancellationToken.None);

        Assert.DoesNotContain(assessment.Signals, signal => signal.Code == "ADDRESS_MISMATCH");
    }

    [Fact]
    public async Task AMissingAddressIsUnknownRatherThanMismatched()
    {
        // Before Milestone 71 no order carried an address - scoring absence as a mismatch would flag every one of them.
        var now = DateTimeOffset.UtcNow;
        await SeedAsync("legacy", 50m, now.AddDays(-40), postalPrefix: "01");

        var noAddressGiven = await Evaluator().EvaluateAsync("legacy", 50m, "", now, CancellationToken.None);
        Assert.DoesNotContain(noAddressGiven.Signals, signal => signal.Code == "ADDRESS_MISMATCH");

        await SeedAsync("noHistory", 50m, now.AddDays(-40), postalPrefix: "");
        var noHistoryOfAddresses = await Evaluator().EvaluateAsync("noHistory", 50m, "66", now, CancellationToken.None);
        Assert.DoesNotContain(noHistoryOfAddresses.Signals, signal => signal.Code == "ADDRESS_MISMATCH");
    }

    [Fact]
    public async Task ABrandNewAccountBuyingFastIsDeclinedByTheCombination()
    {
        // Neither signal declines alone (35 and 25 are under 60) - a minutes-old account on its fourth order is what the two together catch.
        var now = DateTimeOffset.UtcNow;
        await SeedAsync("burst", 40m, now.AddMinutes(-1));
        await SeedAsync("burst", 40m, now.AddMinutes(-2));
        await SeedAsync("burst", 40m, now.AddMinutes(-3));

        var assessment = await Evaluator().EvaluateAsync("burst", 40m, "01", now, CancellationToken.None);

        Assert.False(assessment.Approved);
        Assert.Equal(60, assessment.Score);
        Assert.Contains(assessment.Signals, signal => signal.Code == "VELOCITY");
        Assert.Contains(assessment.Signals, signal => signal.Code == "NEW_ACCOUNT");
    }
}
