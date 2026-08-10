using Cart.Service.Endpoints;

namespace Cart.UnitTests;

/// <summary>
/// CartEndpoints.BuildClientState turns an offline client's
/// batch of Increase/Decrease/Remove operations into a CartCrdtState (or
/// rejects the batch) - the reconciliation algorithm behind POST
/// /carts/me/merge, and the only part of it that doesn't need a live Redis.
/// </summary>
public sealed class CartEndpointsMergeTests
{
    private static CartMergeOperation Increase(string sku, int delta = 1) =>
        new(sku, "Increase", delta, ProductName: "Livro", UnitPrice: 39.90m, Currency: "BRL");

    [Fact]
    public void NoOperationsIsRejected()
    {
        var (state, errors) = CartEndpoints.BuildClientState(null, dotCounterSeed: 0);

        Assert.Null(state);
        Assert.NotNull(errors);
        Assert.Contains("operations", errors.Keys);
    }

    [Fact]
    public void AnEmptyOperationsListIsRejected()
    {
        var (state, errors) = CartEndpoints.BuildClientState([], dotCounterSeed: 0);

        Assert.Null(state);
        Assert.NotNull(errors);
    }

    [Fact]
    public void AnOperationMissingAtSkuIsRejected()
    {
        var (state, errors) = CartEndpoints.BuildClientState([Increase("")], dotCounterSeed: 0);

        Assert.Null(state);
        Assert.NotNull(errors);
        Assert.Contains("operations", errors.Keys);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void AnIncreaseWithANonPositiveDeltaIsRejected(int delta)
    {
        var (state, errors) = CartEndpoints.BuildClientState([Increase("SKU-A", delta)], dotCounterSeed: 0);

        Assert.Null(state);
        Assert.NotNull(errors);
    }

    [Fact]
    public void AnIncreaseMissingProductMetadataIsRejected()
    {
        var missingPrice = new CartMergeOperation("SKU-A", "Increase", 1, ProductName: "Livro", UnitPrice: null, Currency: "BRL");

        var (state, errors) = CartEndpoints.BuildClientState([missingPrice], dotCounterSeed: 0);

        Assert.Null(state);
        Assert.NotNull(errors);
    }

    [Fact]
    public void ADecreaseWithANonPositiveDeltaIsRejected()
    {
        var zeroDelta = new CartMergeOperation("SKU-A", "Decrease", 0);

        var (state, errors) = CartEndpoints.BuildClientState([zeroDelta], dotCounterSeed: 0);

        Assert.Null(state);
        Assert.NotNull(errors);
    }

    [Fact]
    public void AnUnrecognizedKindIsRejected()
    {
        var bogus = new CartMergeOperation("SKU-A", "Overwrite");

        var (state, errors) = CartEndpoints.BuildClientState([bogus], dotCounterSeed: 0);

        Assert.Null(state);
        Assert.NotNull(errors);
        Assert.Contains("SKU-A", errors["operations"][0]);
    }

    [Fact]
    public void AValidBatchOfMixedOperationsFoldsIntoOneState()
    {
        var operations = new[]
        {
            Increase("SKU-A", 2),
            new CartMergeOperation("SKU-B", "Decrease", 1),
            new CartMergeOperation("SKU-C", "Remove")
        };

        var (state, errors) = CartEndpoints.BuildClientState(operations, dotCounterSeed: 0);

        Assert.Null(errors);
        Assert.NotNull(state);
        // SKU-A is the only operation that ever added anything present.
        var line = Assert.Single(state.ToLineItems());
        Assert.Equal("SKU-A", line.Sku);
        Assert.Equal(2, line.Quantity);
    }

    [Fact]
    public void EachIncreaseInTheSameBatchMintsItsOwnDot()
    {
        // Two Increases on the same sku in one batch must both count, not collapse into one.
        var operations = new[] { Increase("SKU-A", 1), Increase("SKU-A", 1) };

        var (state, _) = CartEndpoints.BuildClientState(operations, dotCounterSeed: 0);

        Assert.Equal(2, state!.ToLineItems().Single().Quantity);
    }
}
