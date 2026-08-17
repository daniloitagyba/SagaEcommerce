using Inventory.Service.Domain;
using Microsoft.EntityFrameworkCore;

namespace Inventory.Service.Data;

/// <summary>Seeds inventory stock quantities mirroring Catalog.Service's seeded SKUs.</summary>
public static class InventorySeeder
{
    private static readonly (string Sku, int Quantity)[] SeedData =
    [
        ("SKU-ELEC-001", 50),
        ("SKU-ELEC-002", 8),
        ("SKU-ELEC-003", 30),
        ("SKU-BOOK-001", 100),
        ("SKU-BOOK-002", 100),
        ("SKU-CLTH-001", 40),
        ("SKU-CLTH-002", 40),
        ("SKU-HOME-001", 25),
        ("SKU-HOME-002", 25)
    ];

    public static async Task SeedAsync(InventoryDbContext dbContext, CancellationToken cancellationToken)
    {
        if (await dbContext.InventoryItems.AnyAsync(cancellationToken))
        {
            return;
        }

        var now = DateTimeOffset.UtcNow;
        foreach (var (sku, quantity) in SeedData)
        {
            dbContext.InventoryItems.Add(InventoryItem.Create(sku, quantity, now));
        }

        await dbContext.SaveChangesAsync(cancellationToken);
    }
}
