namespace Catalog.Service.Domain;

/// <summary>
/// MongoDB-backed rather than another Postgres table, since
/// product attributes are genuinely heterogeneous per category (a t-shirt
/// has size/color, a laptop has RAM/CPU) - a relational schema means EAV,
/// JSONB, or one table per category, all worse fits than a document.
/// Attributes stays a flat string/string map to keep the API simple.
/// The Id-as-ObjectId mapping moved to a BsonClassMap in
/// Catalog.Service.Data, so this type carries no MongoDB.Bson dependency,
/// matching the domain-purity rule Orders.Domain enforces.
/// </summary>
public sealed class Product
{
    private const int MaxNameLength = 200;
    private const int MaxDescriptionLength = 4000;
    private const int MaxAttributeCount = 50;
    private const int MaxAttributeKeyLength = 100;
    private const int MaxAttributeValueLength = 500;
    private const int MaxImageCount = 10;
    private const int MaxImageLength = 2_000_000;

    public static Product Create(
        string name,
        string description,
        string categorySlug,
        decimal price,
        string currency,
        string sku,
        Dictionary<string, string>? attributes,
        IReadOnlyList<string>? images,
        DateTimeOffset createdAt)
    {
        var product = new Product
        {
            Name = name?.Trim() ?? string.Empty,
            Description = description?.Trim() ?? string.Empty,
            CategorySlug = categorySlug?.Trim() ?? string.Empty,
            Price = price,
            Currency = currency ?? string.Empty,
            Sku = sku?.Trim() ?? string.Empty,
            Attributes = attributes is null
                ? []
                : new Dictionary<string, string>(attributes, StringComparer.Ordinal),
            Images = images is null ? [] : [.. images],
            CreatedAt = createdAt
        };

        product.EnsureValid();
        return product;
    }

    /// <summary>
    /// Called from Create and from ProductRepository's write path - the
    /// only other construction route is CatalogSeeder's object-initializer
    /// syntax (every setter here is public, matching MongoDB.Driver's own
    /// POCO mapping conventions), which bypassed Create and every invariant
    /// below entirely. Also normalizes Sku/Currency casing in place so
    /// "sku-001" and "SKU-001" collide against the unique index instead of
    /// silently persisting as two distinct documents.
    /// </summary>
    public void EnsureValid()
    {
        if (string.IsNullOrWhiteSpace(Name) || Name.Length > MaxNameLength)
        {
            throw new ArgumentException($"Name is required and must be at most {MaxNameLength} characters.");
        }

        if (Description.Length > MaxDescriptionLength)
        {
            throw new ArgumentException($"Description must be at most {MaxDescriptionLength} characters.");
        }

        if (string.IsNullOrWhiteSpace(CategorySlug))
        {
            throw new ArgumentException("CategorySlug is required.");
        }

        if (Price <= 0m)
        {
            throw new ArgumentOutOfRangeException(nameof(Price), "Price must be positive.");
        }

        if (string.IsNullOrWhiteSpace(Sku))
        {
            throw new ArgumentException("Sku is required.");
        }

        if (Attributes.Count > MaxAttributeCount
            || Attributes.Any(pair => pair.Key.Length > MaxAttributeKeyLength || pair.Value.Length > MaxAttributeValueLength))
        {
            throw new ArgumentException(
                $"Attributes must have at most {MaxAttributeCount} entries, with keys up to {MaxAttributeKeyLength} and values up to {MaxAttributeValueLength} characters.");
        }

        if (Images.Count > MaxImageCount || Images.Any(image => image.Length > MaxImageLength))
        {
            throw new ArgumentException($"Images must have at most {MaxImageCount} entries, each up to {MaxImageLength} characters.");
        }

        var normalizedCurrency = string.IsNullOrWhiteSpace(Currency) ? "BRL" : Currency.Trim().ToUpperInvariant();
        try
        {
            NodaMoney.Currency.FromCode(normalizedCurrency);
        }
        catch (ArgumentException)
        {
            throw new ArgumentException($"'{Currency}' is not a known currency code.");
        }

        Name = Name.Trim();
        Description = Description.Trim();
        CategorySlug = CategorySlug.Trim();
        Sku = Sku.Trim().ToUpperInvariant();
        Currency = normalizedCurrency;
    }

    public string Id { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;

    public string Description { get; set; } = string.Empty;

    public string CategorySlug { get; set; } = string.Empty;

    public decimal Price { get; set; }

    public string Currency { get; set; } = "BRL";

    public string Sku { get; set; } = string.Empty;

    public Dictionary<string, string> Attributes { get; set; } = [];

    public IReadOnlyList<string> Images { get; set; } = [];

    public DateTimeOffset CreatedAt { get; set; }
}
