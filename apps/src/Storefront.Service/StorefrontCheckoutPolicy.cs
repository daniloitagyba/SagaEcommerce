namespace Storefront.Service;

/// <summary>Pure checkout mapping and idempotency policy, testable without an HttpContext.</summary>
internal static class StorefrontCheckoutPolicy
{
    public static StorefrontEndpoints.CheckoutOrderRequest BuildOrderRequest(
        StorefrontEndpoints.CartSnapshot cart,
        StorefrontEndpoints.CheckoutRequest request) =>
        new(
            [.. cart.Items.Select(item => new StorefrontEndpoints.CheckoutOrderItem(item.Sku, item.Quantity))],
            string.IsNullOrWhiteSpace(request.CouponCode) ? null : request.CouponCode,
            request.PaymentMethod,
            request.ShippingAddress,
            cart.Items.Sum(item => item.UnitPrice * item.Quantity));

    public static string? BuildIdempotencyKey(string authorization, StorefrontEndpoints.CartSnapshot cart)
    {
        var subject = UnverifiedJwt.TryGetClaim(authorization, "preferred_username")
            ?? UnverifiedJwt.TryGetClaim(authorization, "sub");
        return subject is null ? null : $"checkout:{subject}:{cart.CartId}:{cart.Version}";
    }
}
