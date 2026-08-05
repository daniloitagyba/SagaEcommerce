using System.Net;
using System.Net.Http.Json;

namespace BuildingBlocks;

public sealed record CatalogProductSnapshot(string Id, string Name, decimal Price, string Currency, string Sku, string CategorySlug);

public interface ICatalogClient
{
    Task<CatalogProductSnapshot?> FindBySkuAsync(string sku, CancellationToken cancellationToken);
}

/// <summary>
/// Shared by Cart.Service (Milestone 42 - a synchronous product snapshot to
/// price a cart line item) and Orders.Worker (Milestone 44 - a category
/// slug lookup for category-scoped bestseller tracking), both calling
/// Catalog.Service's GET /products/by-sku/{sku}. Orders.Worker tolerates
/// this client's absence - see BestsellersStore's "best-effort" note; a
/// Catalog outage degrades bestseller tracking to global-only, not a
/// failed saga.
/// </summary>
public sealed class CatalogClient(HttpClient httpClient) : ICatalogClient
{
    public async Task<CatalogProductSnapshot?> FindBySkuAsync(string sku, CancellationToken cancellationToken)
    {
        using var response = await httpClient.GetAsync($"/products/by-sku/{Uri.EscapeDataString(sku)}", cancellationToken);

        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }

        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<CatalogProductSnapshot>(cancellationToken);
    }
}
