namespace BuildingBlocks;

public sealed class CatalogClientOptions
{
    public const string SectionName = "Catalog";

    public string BaseUrl { get; init; } = "http://localhost:5080";
}
