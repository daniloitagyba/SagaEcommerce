using Cart.Service.Domain;

namespace Cart.UnitTests;

public sealed class CartSnapshotTests
{
    [Fact]
    public void PropertiesRoundTrip()
    {
        var item = new CartLineItem("sku-1", 2, 9.99m, "USD", "Widget", DateTimeOffset.UtcNow);
        var snapshot = new CartSnapshot("cart-1", [item], TimeSpan.FromMinutes(30), 3, CartCrdtState.Empty);

        Assert.Equal("cart-1", snapshot.CartId);
        Assert.Same(item, Assert.Single(snapshot.Items));
        Assert.Equal(TimeSpan.FromMinutes(30), snapshot.TimeToLive);
        Assert.Equal(3, snapshot.Version);
        Assert.Same(CartCrdtState.Empty, snapshot.State);
    }
}
