namespace BuildingBlocks;

public sealed record CatalogProductSnapshot(string Id, string Name, decimal Price, string Currency, string Sku, string CategorySlug);

public interface ICatalogClient
{
    Task<CatalogProductSnapshot?> FindBySkuAsync(string sku, CancellationToken cancellationToken);
}
