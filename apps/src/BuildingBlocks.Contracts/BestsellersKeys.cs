namespace BuildingBlocks;

/// <summary>Cache key shapes shared between the bestsellers writer and reader across services.</summary>
public static class BestsellersKeys
{
    public static string Global => "bestsellers:global";

    public static string Category(string categorySlug) => $"bestsellers:category:{categorySlug}";
}
