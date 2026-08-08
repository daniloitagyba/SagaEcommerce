using Inventory.Service.Domain;

namespace Inventory.IntegrationTests;

/// <summary>PurchaseOrder's own state machine, pure and needing no database.</summary>
public class PurchaseOrderTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 8, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void CreateStartsInRequested()
    {
        var purchaseOrder = PurchaseOrder.Create("SKU-A", "WH-SP", 20, "correlation-1", Now);

        Assert.Equal(PurchaseOrderStates.Requested, purchaseOrder.State);
        Assert.Null(purchaseOrder.ReceivedAt);
    }

    [Fact]
    public void TryReceiveMovesToReceivedExactlyOnce()
    {
        var purchaseOrder = PurchaseOrder.Create("SKU-A", "WH-SP", 20, "correlation-1", Now);
        var receivedAt = Now.AddMinutes(1);

        Assert.True(purchaseOrder.TryReceive(receivedAt));
        Assert.Equal(PurchaseOrderStates.Received, purchaseOrder.State);
        Assert.Equal(receivedAt, purchaseOrder.ReceivedAt);

        // A redelivered receive must not move the timestamp or double-restock.
        Assert.False(purchaseOrder.TryReceive(Now.AddMinutes(2)));
        Assert.Equal(receivedAt, purchaseOrder.ReceivedAt);
    }
}
