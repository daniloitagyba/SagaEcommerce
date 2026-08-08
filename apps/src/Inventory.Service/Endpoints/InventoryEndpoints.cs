using Inventory.Service.Data;
using Microsoft.EntityFrameworkCore;

namespace Inventory.Service.Endpoints;

/// <summary>Milestone 84: what an unauthenticated caller sees instead of an exact count.</summary>
public enum AvailabilityBand
{
    OutOfStock,
    Low,
    InStock
}

public static class InventoryEndpoints
{
    /// <summary>At or below this many units left, an unauthenticated caller sees "Low" rather than an exact count that's still positive.</summary>
    private const int LowStockThreshold = 5;

    public static IEndpointRouteBuilder MapInventoryEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/inventory/{sku}", GetBySkuAsync).WithTags("Inventory");
        // Milestone 84: exact quantities across the whole catalog is what a
        // competitor's scraper wants (sell-through rate); the per-SKU
        // lookup below stays open, coarsened, since a shopper checking one
        // product's availability is not the same threat.
        endpoints.MapGet("/inventory", ListAsync).WithTags("Inventory").RequireAuthorization("inventory:read");
        // Milestone 88: what Orders.Worker's anti-entropy sweeper
        // cross-checks backorders against. Deliberately unauthenticated,
        // unlike the full inventory listing above - Orders.Worker has no
        // Keycloak client credentials of its own to call it with (this
        // pass did not extend Milestone 26's JWT wiring to a
        // service-to-service caller that never needed one before), the
        // same named gap as Payments.Service's new /payments/by-order
        // endpoint. Backorder rows carry no price or margin information,
        // so the exposure this leaves is smaller than the full listing's -
        // still worth closing properly rather than left as the default.
        endpoints.MapGet("/inventory/backorders", ListBackordersAsync).WithTags("Inventory");

        return endpoints;
    }

    private static async Task<IResult> GetBySkuAsync(
        string sku,
        InventoryDbContext dbContext,
        HttpContext httpContext,
        CancellationToken cancellationToken)
    {
        var item = await dbContext.InventoryItems.AsNoTracking().FirstOrDefaultAsync(i => i.Sku == sku, cancellationToken);
        if (item is null)
        {
            return Results.NotFound();
        }

        if (httpContext.User.IsInRole("inventory:read"))
        {
            return Results.Ok(new { item.Sku, item.AvailableQuantity, item.ReservedQuantity, item.UpdatedAt });
        }

        return Results.Ok(new { item.Sku, availability = DescribeBand(item.AvailableQuantity) });
    }

    private static async Task<IResult> ListAsync(
        InventoryDbContext dbContext,
        CancellationToken cancellationToken)
    {
        var items = await dbContext.InventoryItems
            .AsNoTracking()
            .OrderBy(i => i.Sku)
            .Select(i => new { i.Sku, i.AvailableQuantity, i.ReservedQuantity, i.UpdatedAt })
            .ToListAsync(cancellationToken);

        return Results.Ok(items);
    }

    private static async Task<IResult> ListBackordersAsync(
        InventoryDbContext dbContext,
        CancellationToken cancellationToken)
    {
        var backorders = await dbContext.Backorders
            .AsNoTracking()
            .OrderBy(b => b.RequestedAt)
            .Select(b => new { b.ReservationId, b.OrderId, b.Sku, b.Quantity, b.RequestedAt })
            .ToListAsync(cancellationToken);

        return Results.Ok(backorders);
    }

    private static string DescribeBand(int availableQuantity) => availableQuantity switch
    {
        <= 0 => nameof(AvailabilityBand.OutOfStock),
        <= LowStockThreshold => nameof(AvailabilityBand.Low),
        _ => nameof(AvailabilityBand.InStock)
    };
}
