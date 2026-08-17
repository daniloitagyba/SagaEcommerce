using BuildingBlocks;
using Orders.Domain;

namespace Orders.UnitTests;

/// <summary>Pins Orders.Domain.OrderStatusNames to BuildingBlocks.OrderStatuses' Created/Delivered constants across the deliberate assembly boundary; regression coverage for docs/architecture/audit-2026-08-15-domain-and-business-rules-review.md finding 5.</summary>
public sealed class OrderStatusNamesTests
{
    [Fact]
    public void CreatedMirrorsBuildingBlocksOrderStatuses() =>
        Assert.Equal(OrderStatuses.Created, OrderStatusNames.Created);

    [Fact]
    public void DeliveredMirrorsBuildingBlocksOrderStatuses() =>
        Assert.Equal(OrderStatuses.Delivered, OrderStatusNames.Delivered);
}
