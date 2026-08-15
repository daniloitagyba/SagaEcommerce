using Cart.Service.Domain;

namespace Cart.UnitTests;

/// <summary>
/// CartCrdtState composes CartItemCrdt key-wise across a
/// whole cart - these pin the map-level behaviour specifically (which SKU
/// a merge affects, that unrelated SKUs are untouched, that ToLineItems
/// only ever surfaces what's actually live) on top of the per-item CRDT
/// laws CartItemCrdtPropertyTests already proves.
/// </summary>
public class CartCrdtStateTests
{
    private static readonly CartItemMetadata Book = new("Livro", 39.90m, "BRL", DateTimeOffset.UtcNow);

    [Fact]
    public void MergingTwoDisjointSkusKeepsBoth()
    {
        var a = CartCrdtState.Empty.Increase("SKU-A", "replica-a", 1, 0, Book);
        var b = CartCrdtState.Empty.Increase("SKU-B", "replica-b", 2, 0, Book);

        var merged = CartCrdtState.Merge(a, b);
        var lines = merged.ToLineItems().ToDictionary(item => item.Sku);

        Assert.Equal(2, lines.Count);
        Assert.Equal(1, lines["SKU-A"].Quantity);
        Assert.Equal(2, lines["SKU-B"].Quantity);
    }

    [Fact]
    public void RemovingOneSkuLeavesOthersInTheCart()
    {
        var state = CartCrdtState.Empty
            .Increase("SKU-A", "replica-a", 1, 0, Book)
            .Increase("SKU-B", "replica-a", 1, 1, Book)
            .Remove("SKU-A");

        var lines = state.ToLineItems();

        Assert.Single(lines);
        Assert.Equal("SKU-B", lines[0].Sku);
    }

    [Fact]
    public void MetadataIsSnapshottedOnlyOnceEvenAcrossAFullRemoveAndReAdd()
    {
        var firstAdd = new CartItemMetadata("Livro Original", 39.90m, "BRL", DateTimeOffset.UtcNow);
        var state = CartCrdtState.Empty.Increase("SKU-A", "replica-a", 1, 0, firstAdd);

        // A second Increase while the item is still present must not re-snapshot metadata.
        var reinforced = state.Increase("SKU-A", "replica-a", 1, 1, new CartItemMetadata("Different Name", 999m, "BRL", DateTimeOffset.UtcNow));

        Assert.Equal("Livro Original", reinforced.ToLineItems().Single().ProductName);
    }

    [Fact]
    public void RefreshMetadataOverwritesPriceAndNameWithoutTouchingQuantity()
    {
        var state = CartCrdtState.Empty.Increase("SKU-A", "replica-a", 3, 0, Book);

        var refreshed = state.RefreshMetadata("SKU-A", new CartItemMetadata("Livro (2ª edição)", 44.90m, "BRL", DateTimeOffset.UtcNow));

        var line = refreshed.ToLineItems().Single();
        Assert.Equal(44.90m, line.UnitPrice);
        Assert.Equal("Livro (2ª edição)", line.ProductName);
        Assert.Equal(3, line.Quantity);
    }

    [Fact]
    public void RefreshMetadataOnASkuNotInTheCartIsANoOp()
    {
        var state = CartCrdtState.Empty.Increase("SKU-A", "replica-a", 1, 0, Book);

        var refreshed = state.RefreshMetadata("SKU-DOES-NOT-EXIST", new CartItemMetadata("Ghost", 1m, "BRL", DateTimeOffset.UtcNow));

        Assert.Same(state, refreshed);
        Assert.DoesNotContain(refreshed.ToLineItems(), item => item.Sku == "SKU-DOES-NOT-EXIST");
    }

    [Fact]
    public void RefreshMetadataOnARemovedSkuIsANoOp()
    {
        var state = CartCrdtState.Empty
            .Increase("SKU-A", "replica-a", 1, 0, Book)
            .Remove("SKU-A");

        var refreshed = state.RefreshMetadata("SKU-A", new CartItemMetadata("Should Not Resurrect", 1m, "BRL", DateTimeOffset.UtcNow));

        Assert.Same(state, refreshed);
        Assert.Empty(refreshed.ToLineItems());
    }

    [Fact]
    public void RefreshMetadataLeavesOtherSkusUntouched()
    {
        var state = CartCrdtState.Empty
            .Increase("SKU-A", "replica-a", 1, 0, Book)
            .Increase("SKU-B", "replica-a", 2, 1, Book);

        var refreshed = state.RefreshMetadata("SKU-A", new CartItemMetadata("New Name", 10m, "BRL", DateTimeOffset.UtcNow));

        var lines = refreshed.ToLineItems().ToDictionary(item => item.Sku);
        Assert.Equal("Livro", lines["SKU-B"].ProductName);
        Assert.Equal(2, lines["SKU-B"].Quantity);
    }

    [Fact]
    public void AConcurrentAddOnOneReplicaSurvivesARemoveOnAnotherThatNeverSawIt()
    {
        // The map-level version of CartItemCrdtPropertyTests'
        // add-wins case: replica A never learned about SKU-A at all
        // (empty state), so its "remove everything I've seen" is a no-op
        // for it; replica B added it. The merge must keep it.
        var replicaAknowsNothing = CartCrdtState.Empty.Remove("SKU-A");
        var replicaBAdded = CartCrdtState.Empty.Increase("SKU-A", "replica-b", 3, 0, Book);

        var merged = CartCrdtState.Merge(replicaAknowsNothing, replicaBAdded);

        var line = Assert.Single(merged.ToLineItems());
        Assert.Equal(3, line.Quantity);
    }
}
