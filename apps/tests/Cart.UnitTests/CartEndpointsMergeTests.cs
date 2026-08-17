using Cart.Service.Endpoints;

namespace Cart.UnitTests;

/// <summary>CartEndpoints.BuildClientState turns an offline client's batch of Increase/Decrease/Remove operations into a CartCrdtState (or rejects the batch) - the reconciliation algorithm behind POST /carts/me/merge, testable without a live Redis.</summary>
public sealed class CartEndpointsMergeTests
{
    private static CartMergeOperation Increase(string sku, int delta = 1) =>
        new(sku, "Increase", delta, ProductName: "Livro", UnitPrice: 39.90m, Currency: "BRL", OperationId: Guid.NewGuid());

    [Fact]
    public void NoOperationsIsRejected()
    {
        var (state, errors) = CartEndpoints.BuildClientState(null);

        Assert.Null(state);
        Assert.NotNull(errors);
        Assert.Contains("operations", errors.Keys);
    }

    [Fact]
    public void AnEmptyOperationsListIsRejected()
    {
        var (state, errors) = CartEndpoints.BuildClientState([]);

        Assert.Null(state);
        Assert.NotNull(errors);
    }

    [Fact]
    public void AnOperationMissingAtSkuIsRejected()
    {
        var (state, errors) = CartEndpoints.BuildClientState([Increase("")]);

        Assert.Null(state);
        Assert.NotNull(errors);
        Assert.Contains("operations", errors.Keys);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void AnIncreaseWithANonPositiveDeltaIsRejected(int delta)
    {
        var (state, errors) = CartEndpoints.BuildClientState([Increase("SKU-A", delta)]);

        Assert.Null(state);
        Assert.NotNull(errors);
    }

    [Fact]
    public void AnIncreaseMissingProductMetadataIsRejected()
    {
        var missingPrice = new CartMergeOperation("SKU-A", "Increase", 1, ProductName: "Livro", UnitPrice: null, Currency: "BRL", OperationId: Guid.NewGuid());

        var (state, errors) = CartEndpoints.BuildClientState([missingPrice]);

        Assert.Null(state);
        Assert.NotNull(errors);
    }

    [Fact]
    public void ADecreaseWithANonPositiveDeltaIsRejected()
    {
        var zeroDelta = new CartMergeOperation("SKU-A", "Decrease", 0, OperationId: Guid.NewGuid());

        var (state, errors) = CartEndpoints.BuildClientState([zeroDelta]);

        Assert.Null(state);
        Assert.NotNull(errors);
    }

    [Fact]
    public void AnUnrecognizedKindIsRejected()
    {
        var bogus = new CartMergeOperation("SKU-A", "Overwrite", OperationId: Guid.NewGuid());

        var (state, errors) = CartEndpoints.BuildClientState([bogus]);

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
            new CartMergeOperation("SKU-B", "Decrease", 1, OperationId: Guid.NewGuid()),
            new CartMergeOperation(
                "SKU-C",
                "Remove",
                OperationId: Guid.NewGuid(),
                ObservedDots: [new CartObservedDot("server", 1)])
        };

        var (state, errors) = CartEndpoints.BuildClientState(operations);

        Assert.Null(errors);
        Assert.NotNull(state);
        var line = Assert.Single(state.ToLineItems());
        Assert.Equal("SKU-A", line.Sku);
        Assert.Equal(2, line.Quantity);
    }

    [Fact]
    public void EachIncreaseInTheSameBatchMintsItsOwnDot()
    {
        var operations = new[] { Increase("SKU-A", 1), Increase("SKU-A", 1) };

        var (state, _) = CartEndpoints.BuildClientState(operations);

        Assert.Equal(2, state!.ToLineItems().Single().Quantity);
    }

    [Fact]
    public void SeparateOperationsContributeIndependentlyWhileAReplayIsIdempotent()
    {
        var first = Increase("SKU-A", 1);
        var second = Increase("SKU-A", 1);
        var (firstState, _) = CartEndpoints.BuildClientState([first]);
        var (secondState, _) = CartEndpoints.BuildClientState([second]);
        var (replayedFirstState, _) = CartEndpoints.BuildClientState([first]);

        var afterTwoOperations = Cart.Service.Domain.CartCrdtState.Merge(firstState!, secondState!);
        var afterReplay = Cart.Service.Domain.CartCrdtState.Merge(afterTwoOperations, replayedFirstState!);

        Assert.Equal(2, afterReplay.ToLineItems().Single().Quantity);
    }

    [Fact]
    public void MissingOrDuplicateOperationIdsAreRejected()
    {
        var operationId = Guid.NewGuid();
        var missing = new CartMergeOperation(
            "SKU-A", "Increase", 1, "Livro", 39.90m, "BRL", OperationId: Guid.Empty);
        var duplicateA = new CartMergeOperation(
            "SKU-A", "Increase", 1, "Livro", 39.90m, "BRL", operationId);
        var duplicateB = new CartMergeOperation(
            "SKU-B", "Increase", 1, "Livro", 39.90m, "BRL", operationId);

        Assert.NotNull(CartEndpoints.BuildClientState([missing]).Errors);
        Assert.NotNull(CartEndpoints.BuildClientState([duplicateA, duplicateB]).Errors);
    }

    [Fact]
    public void ARemoveWithoutCausalContextIsRejected()
    {
        var remove = new CartMergeOperation("SKU-A", "Remove", OperationId: Guid.NewGuid());

        var (state, errors) = CartEndpoints.BuildClientState([remove]);

        Assert.Null(state);
        Assert.NotNull(errors);
    }

    [Fact]
    public void ARemoveTombstonesObservedDotsButPreservesAConcurrentAdd()
    {
        var observed = new Cart.Service.Domain.CartDot("server", 1);
        var concurrent = new Cart.Service.Domain.CartDot("other-device", 2);
        var serverState = new Cart.Service.Domain.CartCrdtState(
            new Dictionary<string, Cart.Service.Domain.CartItemCrdt>
            {
                ["SKU-A"] = new(
                    new HashSet<Cart.Service.Domain.CartDot> { observed, concurrent },
                    new HashSet<Cart.Service.Domain.CartDot>(),
                    new Dictionary<string, (long, long)>
                    {
                        ["server"] = (1, 0),
                        ["other-device"] = (1, 0)
                    })
            },
            new Dictionary<string, Cart.Service.Domain.CartItemMetadata>
            {
                ["SKU-A"] = new("Livro", 39.90m, "BRL", DateTimeOffset.UnixEpoch)
            });
        var remove = new CartMergeOperation(
            "SKU-A",
            "Remove",
            OperationId: Guid.NewGuid(),
            ObservedDots: [new CartObservedDot(observed.ReplicaId, observed.Counter)]);

        var (clientState, errors) = CartEndpoints.BuildClientState([remove]);
        var merged = Cart.Service.Domain.CartCrdtState.Merge(serverState, clientState!);

        Assert.Null(errors);
        Assert.True(merged.Items["SKU-A"].IsPresent);
        Assert.DoesNotContain(observed, merged.Items["SKU-A"].LiveDots);
        Assert.Contains(concurrent, merged.Items["SKU-A"].LiveDots);
    }
}
