namespace Catalog.Service.Domain;

/// <summary>
/// Milestone 40: MongoDB-backed rather than another Postgres table, since
/// product attributes are genuinely heterogeneous per category (a t-shirt
/// has size/color, a laptop has RAM/CPU) - a relational schema means EAV,
/// JSONB, or one table per category, all worse fits than a document.
/// Attributes stays a flat string/string map to keep the API simple.
/// Milestone 61: the Id-as-ObjectId mapping moved to a BsonClassMap in
/// Catalog.Service.Data, so this type carries no MongoDB.Bson dependency,
/// matching the domain-purity rule Orders.Domain enforces.
/// </summary>
public sealed class Product
{
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
