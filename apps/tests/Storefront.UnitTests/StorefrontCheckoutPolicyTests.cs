using System.Text;
using Storefront.Service;

namespace Storefront.UnitTests;

public sealed class StorefrontCheckoutPolicyTests
{
    [Fact]
    public void MappingDerivesSubtotalAndNormalizesAnEmptyCoupon()
    {
        var cart = new StorefrontEndpoints.CartSnapshot(
            "cart-1",
            [new StorefrontEndpoints.CartSnapshotItem("SKU-1", 2, 15m)],
            3);

        var result = StorefrontCheckoutPolicy.BuildOrderRequest(
            cart,
            new StorefrontEndpoints.CheckoutRequest(" ", "Pix"));

        Assert.Equal(30m, result.ExpectedSubtotal);
        Assert.Null(result.CouponCode);
        Assert.Equal("SKU-1", Assert.Single(result.Items).Sku);
    }

    [Fact]
    public void IdempotencyKeyIsStableForTheSameShopperAndCartVersion()
    {
        var payload = Convert.ToBase64String(Encoding.UTF8.GetBytes("{\"sub\":\"customer-1\"}"))
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
        var token = $"Bearer header.{payload}.signature";
        var cart = new StorefrontEndpoints.CartSnapshot("cart-1", [], 7);

        var key = StorefrontCheckoutPolicy.BuildIdempotencyKey(token, cart);

        Assert.Equal("checkout:customer-1:cart-1:7", key);
    }
}
