using Inventory.Service.Data;
using Microsoft.EntityFrameworkCore;

namespace Inventory.Service;

/// <summary>Per-SKU advisory-lock namespace shared by every SKU-scoped write in this service.</summary>
internal static class SkuAdvisoryLock
{
    public static async Task AcquireAsync(InventoryDbContext dbContext, string sku, CancellationToken cancellationToken)
    {
        _ = await dbContext.Database.ExecuteSqlInterpolatedAsync(
            $"SELECT pg_advisory_xact_lock(hashtextextended({sku}, 73000001))",
            cancellationToken);
    }
}
