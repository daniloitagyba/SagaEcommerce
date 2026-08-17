namespace Inventory.Service;

public sealed partial class InventoryReservationMessageProcessor
{
    /// <summary>Records a commit's drawn-down quantity or resolves a restock's given-back quantity against the reservation ledger.</summary>
    private static async Task UpdateReservationLedgerAsync(
        WarehouseAllocationStore allocationStore,
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
