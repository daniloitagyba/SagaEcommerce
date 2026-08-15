using Inventory.Service.Data;
using Microsoft.EntityFrameworkCore;

namespace Inventory.Service;

/// <summary>
/// The one per-SKU advisory-lock namespace/seed every SKU-scoped write in
/// this service shares - originally private to
/// InventoryReservationMessageProcessor (reserve/commit/release/restock),
/// pulled out so BackorderTimeoutSweeper can serialize against the exact
/// same lock. Without that, the sweeper's delete of a timed-out backorder
/// and InventoryReservationMessageProcessor.ReleaseBackordersAsync's
/// restock-triggered fill of that same row raced with no lock between them
/// at all - whichever committed second either failed outright (deleting a
/// row the other side already deleted throws DbUpdateConcurrencyException,
/// rolling back a legitimate restock along with it) or, in a narrower
/// interleaving, left a real Inventory allocation for an order the sweeper
/// had already told the saga to give up on.
/// </summary>
internal static class SkuAdvisoryLock
{
    public static async Task AcquireAsync(InventoryDbContext dbContext, string sku, CancellationToken cancellationToken)
    {
        // Transaction-scoped: commit, rollback and process death all release it automatically.
        _ = await dbContext.Database.ExecuteSqlInterpolatedAsync(
            $"SELECT pg_advisory_xact_lock(hashtextextended({sku}, 73000001))",
            cancellationToken);
    }
}
