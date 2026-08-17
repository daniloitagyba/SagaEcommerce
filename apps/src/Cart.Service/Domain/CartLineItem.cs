namespace Cart.Service.Domain;

/// <summary>A cart line; UnitPrice/ProductName/Currency are snapshotted at first add, not re-fetched on quantity changes.</summary>
public sealed record CartLineItem(
    string Sku,
    int Quantity,
    decimal UnitPrice,
    string Currency,
    string ProductName,
    DateTimeOffset AddedAt)
{
    public CartLineItem WithQuantity(int quantity) => this with { Quantity = quantity };
}
