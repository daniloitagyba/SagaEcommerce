namespace Catalog.Service.Domain;

/// <summary>
/// Categories are their own small collection rather than a
/// free-text field on Product - lets the storefront list categories
/// without a distinct-scan over the product collection, and gives the
/// slug a stable identity independent of display-name changes.
/// </summary>
public sealed class Category
{
    public string Id { get; init; } = string.Empty;

    public string Slug { get; init; } = string.Empty;

    public string Name { get; init; } = string.Empty;
}
