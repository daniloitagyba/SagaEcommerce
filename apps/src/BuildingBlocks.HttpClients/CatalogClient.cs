using System.Net;
using System.Net.Http.Json;

namespace BuildingBlocks;

/// <summary>
/// Shared HTTP adapter for services calling Catalog's SKU lookup endpoint.
/// The interface and response contract stay in BuildingBlocks.Contracts;
/// transport details live here at the infrastructure edge.
/// </summary>
public sealed class CatalogClient(HttpClient httpClient) : ICatalogClient
{
    public async Task<CatalogProductSnapshot?> FindBySkuAsync(
        string sku,
        CancellationToken cancellationToken)
    {
        using var response = await httpClient.GetAsync(
            $"/products/by-sku/{Uri.EscapeDataString(sku)}",
            cancellationToken);

        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }

        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<CatalogProductSnapshot>(cancellationToken);
    }
}
