namespace Catalog.Service.Domain;

/// <summary>A product category, stored as its own small collection rather than a free-text field on Product.</summary>
public sealed class Category
{
    internal Category()
    {
    }

    public static Category Create(string id, string slug, string name)
    {
        if (string.IsNullOrWhiteSpace(id))
        {
            throw new ArgumentException("Id is required.", nameof(id));
        }

        if (string.IsNullOrWhiteSpace(slug))
        {
            throw new ArgumentException("Slug is required.", nameof(slug));
        }

        if (string.IsNullOrWhiteSpace(name))
        {
            throw new ArgumentException("Name is required.", nameof(name));
        }

        return new Category
        {
            Id = id.Trim(),
            Slug = slug.Trim(),
            Name = name.Trim()
        };
    }

    public string Id { get; internal set; } = string.Empty;

    public string Slug { get; internal set; } = string.Empty;

    public string Name { get; internal set; } = string.Empty;
}
