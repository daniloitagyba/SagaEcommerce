using Inventory.Service.Data;
using Microsoft.EntityFrameworkCore;

namespace Inventory.Service.Endpoints;

/// <summary>What an unauthenticated caller sees instead of an exact count.</summary>
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
        // Exact quantities across the whole catalog is what a
        // competitor's scraper wants (sell-through rate); the per-SKU
        // lookup below stays open, coarsened, since a shopper checking one
        // product's availability is not the same threat.
        endpoints.MapGet("/inventory", ListAsync).WithTags("Inventory").RequireAuthorization("inventory:read");
        // What Orders.Worker's anti-entropy sweeper
        // cross-checks backorders against.
        // inventory:read-gated, the same policy the full listing above
        // already requires - Orders.Worker's own service account
        // (KeycloakTokenProvider, orders-api-clients) already carries that
        // role, so reusing it here needed no new role, only the requirement itself.
        endpoints.MapGet("/inventory/backorders", ListBackordersAsync).WithTags("Inventory").RequireAuthorization("inventory:read");
        // The permanent ledger's read side - every
        // order that still has committed (not yet restocked) inventory
        // outstanding, for the anti-entropy check a prior audit
        // wanted (committed inventory belonging to a
        // cancelled order) and couldn't build without this table existing.
        endpoints.MapGet("/inventory/committed-reservations", ListCommittedReservationsAsync).WithTags("Inventory").RequireAuthorization("inventory:read");

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

    private static async Task<IResult> ListCommittedReservationsAsync(
        InventoryDbContext dbContext,
        CancellationToken cancellationToken)
    {
        var entries = await dbContext.ReservationLedgerEntries
            .AsNoTracking()
            .OrderBy(e => e.CommittedAt)
            .Select(e => new { e.ReservationId, e.OrderId, e.Sku, e.Quantity, e.CommittedAt })
            .ToListAsync(cancellationToken);

        return Results.Ok(entries);
    }

    private static string DescribeBand(int availableQuantity) => availableQuantity switch
    {
        <= 0 => nameof(AvailabilityBand.OutOfStock),
        <= LowStockThreshold => nameof(AvailabilityBand.Low),
        _ => nameof(AvailabilityBand.InStock)
    };
}
