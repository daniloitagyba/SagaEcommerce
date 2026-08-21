namespace Catalog.Service.Domain;

/// <summary>A catalog product, MongoDB-backed to accommodate heterogeneous per-category attributes.</summary>
public sealed class Product
{
    private const int MaxNameLength = 200;
    private const int MaxDescriptionLength = 4000;
    private const int MaxAttributeCount = 50;
    private const int MaxAttributeKeyLength = 100;
    private const int MaxAttributeValueLength = 500;
    private const int MaxImageCount = 10;
    private const int MaxImageLength = 2_000_000;

    internal Product()
    {
    }

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

    public void UpdateDetails(
        string name,
        string description,
        string categorySlug,
        decimal price,
        string currency,
        string sku,
        Dictionary<string, string>? attributes,
        IReadOnlyList<string>? images)
    {
        Name = name?.Trim() ?? string.Empty;
        Description = description?.Trim() ?? string.Empty;
        CategorySlug = categorySlug?.Trim() ?? string.Empty;
        Price = price;
        Currency = currency ?? string.Empty;
        Sku = sku?.Trim() ?? string.Empty;
        Attributes = attributes is null
            ? []
            : new Dictionary<string, string>(attributes, StringComparer.Ordinal);
        Images = images is null ? [] : [.. images];

        EnsureValid();
    }

    /// <summary>Validates all invariants and normalizes Sku/Currency casing in place.</summary>
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

    public string Id { get; internal set; } = string.Empty;

    public string Name { get; internal set; } = string.Empty;

    public string Description { get; internal set; } = string.Empty;

    public string CategorySlug { get; internal set; } = string.Empty;

    public decimal Price { get; internal set; }

    public string Currency { get; internal set; } = "BRL";

    public string Sku { get; internal set; } = string.Empty;

    public Dictionary<string, string> Attributes { get; internal set; } = [];

    public IReadOnlyList<string> Images { get; internal set; } = [];

    public DateTimeOffset CreatedAt { get; internal set; }
}
