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

/// <summary>Configures product-summary fan-out hedging; HedgeDelayMilliseconds &gt; 0 fires a second Inventory request if the first is slow.</summary>
public sealed class ProductSummaryOptions
{
    public const string SectionName = "ProductSummary";

    public int HedgeDelayMilliseconds { get; init; }
}
