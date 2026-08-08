namespace Cart.Service.Domain;

/// <summary>
/// UnitPrice/ProductName/Currency are snapshotted from Catalog.Service at
/// the moment a SKU is first added to the cart, not re-fetched on every
/// quantity change - a deliberate e-commerce convention: the cart reflects
/// the price the shopper saw when they added the item, not whatever the
/// catalog says right now. Checkout is where prices get
/// revalidated against the current catalog, not here.
/// </summary>
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
