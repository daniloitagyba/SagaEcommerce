namespace BuildingBlocks;

/// <summary>
/// Milestone 44: shared between the writer (Orders.Worker, on a confirmed
/// saga) and the reader (Catalog.Service's bestsellers endpoint) - both
/// sides must agree on the exact key shape without either owning the
/// other's code, the same reason OrderCacheKeys lives here rather than in
/// Orders.Api alone.
/// </summary>
public static class BestsellersKeys
{
    public static string Global => "bestsellers:global";

    public static string Category(string categorySlug) => $"bestsellers:category:{categorySlug}";
}
