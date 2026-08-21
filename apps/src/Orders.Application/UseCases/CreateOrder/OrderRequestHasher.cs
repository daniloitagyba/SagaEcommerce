using System.Security.Cryptography;
using System.Text.Json;

namespace Orders.Application.UseCases.CreateOrder;

internal static class OrderRequestHasher
{
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);

    public static string Compute(CreateOrderCommand command, string normalizedCustomerId)
    {
        var items = command.Items?
            .Select(item => new CanonicalItem(item.Sku!.Trim(), item.Quantity))
            .OrderBy(item => item.Sku, StringComparer.Ordinal)
            .ToArray() ?? [];
        var address = command.ShippingAddress is { } shipping
            ? new CanonicalAddress(
                shipping.Line1.Trim(),
                shipping.City.Trim(),
                shipping.Region.Trim().ToUpperInvariant(),
                shipping.PostalCode.Trim())
            : null;
        var request = new CanonicalRequest(
            normalizedCustomerId,
            command.IsLineItemCheckout,
            command.Amount,
            command.Currency?.Trim().ToUpperInvariant(),
            items,
            command.CouponCode?.Trim().ToUpperInvariant(),
            command.PaymentMethod?.Trim() ?? BuildingBlocks.PaymentMethods.Pix,
            address,
            command.ExpectedSubtotal);

        return Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(request, SerializerOptions)));
    }

    private sealed record CanonicalItem(string Sku, int Quantity);

    private sealed record CanonicalAddress(string Line1, string City, string Region, string PostalCode);

    private sealed record CanonicalRequest(
        string CustomerId,
        bool IsLineItemCheckout,
        decimal Amount,
        string? Currency,
        IReadOnlyList<CanonicalItem> Items,
        string? CouponCode,
        string PaymentMethod,
        CanonicalAddress? ShippingAddress,
        decimal? ExpectedSubtotal);
}
