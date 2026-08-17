namespace Catalog.Service.Domain;

/// <summary>A product category, stored as its own small collection rather than a free-text field on Product.</summary>
public sealed class Category
{
    public string Id { get; init; } = string.Empty;

    public string Slug { get; init; } = string.Empty;

    public string Name { get; init; } = string.Empty;
}
