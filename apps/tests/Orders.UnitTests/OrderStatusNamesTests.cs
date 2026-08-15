using BuildingBlocks;
using Orders.Domain;

namespace Orders.UnitTests;

/// <summary>
/// Orders.Domain.OrderStatusNames mirrors BuildingBlocks.OrderStatuses'
/// Created and Delivered constants across a deliberate assembly boundary
/// (Orders.Domain.csproj takes only NodaMoney) - this pins the two
/// together so a rename of either drifts loudly instead of silently.
/// Regression coverage for
/// docs/architecture/audit-2026-08-15-domain-and-business-rules-review.md's
/// finding 5.
/// </summary>
public sealed class OrderStatusNamesTests
{
    [Fact]
    public void CreatedMirrorsBuildingBlocksOrderStatuses() =>
        Assert.Equal(OrderStatuses.Created, OrderStatusNames.Created);

    [Fact]
    public void DeliveredMirrorsBuildingBlocksOrderStatuses() =>
        Assert.Equal(OrderStatuses.Delivered, OrderStatusNames.Delivered);
}
