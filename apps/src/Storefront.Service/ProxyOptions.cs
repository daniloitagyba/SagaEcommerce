namespace Storefront.Service;

public sealed class CatalogProxyOptions
{
    public const string SectionName = "CatalogProxy";

    public string BaseUrl { get; init; } = "http://localhost:5080";
}

public sealed class CartProxyOptions
{
    public const string SectionName = "CartProxy";

    public string BaseUrl { get; init; } = "http://localhost:5290";
}

public sealed class OrdersProxyOptions
{
    public const string SectionName = "OrdersProxy";

    public string BaseUrl { get; init; } = "http://localhost:5000";
}

public sealed class InventoryProxyOptions
{
    public const string SectionName = "InventoryProxy";

    public string BaseUrl { get; init; } = "http://localhost:5170";
}

/// <summary>
/// Milestone 54: the product-summary endpoint fans out to Catalog and
/// Inventory in parallel and waits for both, so tail latency is at least as
/// bad as the slowest leg (Dean &amp; Barroso's "Tail At Scale").
/// HedgeDelayMilliseconds &gt; 0 fires a second request to Inventory if the
/// first hasn't answered within that delay, taking whichever wins - the
/// same mitigation Milestone 39 proved for GET /orders/{id}. 0 (default)
/// disables hedging.
/// </summary>
public sealed class ProductSummaryOptions
{
    public const string SectionName = "ProductSummary";

    public int HedgeDelayMilliseconds { get; init; }
}
