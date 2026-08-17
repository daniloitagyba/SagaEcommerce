using Inventory.Service.Domain;

namespace Inventory.UnitTests;

public sealed class InventoryReservationLedgerEntryTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 17, 12, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(4)]
    public void ReducingByAnInvalidQuantityIsRejected(int quantity)
    {
        var entry = InventoryReservationLedgerEntry.Create(
            Guid.NewGuid(), Guid.NewGuid(), "SKU-A", 3, Now);

        Assert.Throws<ArgumentOutOfRangeException>(() => entry.Reduce(quantity));

        Assert.Equal(3, entry.Quantity);
    }

    [Fact]
    public void ReducingByTheRemainingQuantityClearsTheEntry()
    {
        var entry = InventoryReservationLedgerEntry.Create(
            Guid.NewGuid(), Guid.NewGuid(), "SKU-A", 3, Now);

        entry.Reduce(3);

        Assert.Equal(0, entry.Quantity);
    }
}
