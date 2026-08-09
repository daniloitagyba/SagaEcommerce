using Microsoft.Extensions.DependencyInjection;

namespace Inventory.Service;

// The permanent reservation ledger's write side, split into its own file
// from InventoryReservationMessageProcessor.cs to stay under the 500-line
// module-size budget - the same reason InventoryReservationMessageProcessor.Backorders.cs
// exists. Same partial class, same private static-ness (this method takes
// its own IServiceProvider rather than closing over the field the rest of
// the class uses, since it was already being called from a static-only
// context inside ProcessSettlementAsync).
public sealed partial class InventoryReservationMessageProcessor
{
    /// <summary>
    /// The permanent ledger's two write sides, split out of
    /// ProcessSettlementAsync itself to keep that method under this
    /// codebase's own complexity gate - a commit records what it actually
    /// drew down; a restock (the "else" of settleAllocation, same branch
    /// the TryRestockAsync call above takes) resolves whatever of it the
    /// order is giving back. Both gated on succeeded, same as the reply
    /// ProcessSettlementAsync builds - nothing recorded or resolved for a
    /// mutation that didn't actually happen.
    /// </summary>
    private static async Task UpdateReservationLedgerAsync(
        IServiceProvider serviceProvider,
        bool succeeded,
        bool settleAllocation,
        bool commitAllocation,
        Guid reservationId,
        Guid orderId,
        string sku,
        int quantity,
        DateTimeOffset processedAt,
        CancellationToken cancellationToken)
    {
        if (!succeeded)
        {
            return;
        }

        var allocationStore = serviceProvider.GetRequiredService<WarehouseAllocationStore>();
        if (settleAllocation && commitAllocation)
        {
            await allocationStore.RecordCommittedAsync(reservationId, orderId, sku, quantity, processedAt, cancellationToken);
        }
        else if (!settleAllocation)
        {
            await allocationStore.ResolveLedgerOnRestockAsync(orderId, sku, quantity, cancellationToken);
        }
    }
}
