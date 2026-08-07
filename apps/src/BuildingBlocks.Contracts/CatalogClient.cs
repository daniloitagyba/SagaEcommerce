using System.Net;
using System.Net.Http.Json;

namespace BuildingBlocks;

public sealed record CatalogProductSnapshot(string Id, string Name, decimal Price, string Currency, string Sku, string CategorySlug);

public interface ICatalogClient
{
    Task<CatalogProductSnapshot?> FindBySkuAsync(string sku, CancellationToken cancellationToken);
}

/// <summary>
/// Shared by Cart.Service (pricing a cart line) and Orders.Worker
/// (category lookup for bestseller tracking), both calling GET
/// /products/by-sku/{sku}. Orders.Worker tolerates this client's absence -
/// see BestsellersStore - degrading to global-only tracking, not a failed saga.
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
