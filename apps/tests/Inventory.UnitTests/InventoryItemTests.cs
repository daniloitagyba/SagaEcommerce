using CsCheck;
using Inventory.Service.Domain;

namespace Inventory.UnitTests;

/// <summary>InventoryItem's own reserve/commit/release/restock guards, pure and needing no database, in isolation from the Kafka/EF wiring.</summary>
public class InventoryItemTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 8, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void CreateStartsFullyAvailableWithNothingReserved()
    {
        var item = InventoryItem.Create("SKU-A", 10, Now);

        Assert.Equal(10, item.AvailableQuantity);
        Assert.Equal(0, item.ReservedQuantity);
    }

    [Fact]
    public void ReservingWithinAvailableStockMovesUnitsFromAvailableToReserved()
    {
        var item = InventoryItem.Create("SKU-A", 10, Now);

        Assert.True(item.TryReserve(4, Now));

        Assert.Equal(6, item.AvailableQuantity);
        Assert.Equal(4, item.ReservedQuantity);
    }

    [Fact]
    public void ReservingMoreThanAvailableFailsAndChangesNothing()
    {
        var item = InventoryItem.Create("SKU-A", 3, Now);

        Assert.False(item.TryReserve(4, Now));

        Assert.Equal(3, item.AvailableQuantity);
        Assert.Equal(0, item.ReservedQuantity);
    }

    [Fact]
    public void CommittingAReservationDrawsDownReservedWithoutTouchingAvailable()
    {
        var item = InventoryItem.Create("SKU-A", 10, Now);
        item.TryReserve(4, Now);

        Assert.True(item.TryCommit(4, Now.AddMinutes(1)));

        Assert.Equal(6, item.AvailableQuantity);
        Assert.Equal(0, item.ReservedQuantity);
    }

    [Fact]
    public void CommittingMoreThanWasReservedFailsAndChangesNothing()
    {
        var item = InventoryItem.Create("SKU-A", 10, Now);
        item.TryReserve(4, Now);

        Assert.False(item.TryCommit(5, Now.AddMinutes(1)));

        Assert.Equal(6, item.AvailableQuantity);
        Assert.Equal(4, item.ReservedQuantity);
    }

    [Fact]
    public void ReleasingAReservationReturnsUnitsToAvailable()
    {
        var item = InventoryItem.Create("SKU-A", 10, Now);
        item.TryReserve(4, Now);

        Assert.True(item.TryRelease(4, Now.AddMinutes(1)));

        Assert.Equal(10, item.AvailableQuantity);
        Assert.Equal(0, item.ReservedQuantity);
    }

    [Fact]
    public void ReleasingMoreThanWasReservedFailsAndChangesNothing()
    {
        var item = InventoryItem.Create("SKU-A", 10, Now);
        item.TryReserve(4, Now);

        Assert.False(item.TryRelease(5, Now.AddMinutes(1)));

        Assert.Equal(6, item.AvailableQuantity);
        Assert.Equal(4, item.ReservedQuantity);
    }

    [Fact]
    public void PartiallyCommittingThenReleasingTheRemainderClearsReservedEntirely()
    {
        var item = InventoryItem.Create("SKU-A", 10, Now);
        item.TryReserve(10, Now);

        Assert.True(item.TryCommit(6, Now.AddMinutes(1)));
        Assert.True(item.TryRelease(4, Now.AddMinutes(2)));

        Assert.Equal(4, item.AvailableQuantity);
        Assert.Equal(0, item.ReservedQuantity);
    }

    [Fact]
    public void RestockingAddsUnitsWithoutTouchingReserved()
    {
        var item = InventoryItem.Create("SKU-A", 10, Now);
        item.TryReserve(4, Now);

        item.Restock(5, Now.AddMinutes(1));

        Assert.Equal(11, item.AvailableQuantity);
        Assert.Equal(4, item.ReservedQuantity);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void RestockingWithANonPositiveQuantityIsRejected(int quantity)
    {
        var item = InventoryItem.Create("SKU-A", 10, Now);

        Assert.Throws<ArgumentOutOfRangeException>(() => item.Restock(quantity, Now.AddMinutes(1)));

        Assert.Equal(10, item.AvailableQuantity);
        Assert.Equal(Now, item.UpdatedAt);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void NonPositiveReservationsAndSettlementsDoNotChangeStock(int quantity)
    {
        var item = InventoryItem.Create("SKU-A", 10, Now);
        item.TryReserve(4, Now);

        Assert.False(item.TryReserve(quantity, Now.AddMinutes(1)));
        Assert.False(item.TryCommit(quantity, Now.AddMinutes(1)));
        Assert.False(item.TryRelease(quantity, Now.AddMinutes(1)));

        Assert.Equal(6, item.AvailableQuantity);
        Assert.Equal(4, item.ReservedQuantity);
    }

    [Fact]
    public void RandomInventoryOperationsPreserveNonNegativeBalances()
    {
        Gen.Int[0, int.MaxValue].Sample(seed =>
        {
            var random = new Random(seed);
            var item = InventoryItem.Create("SKU-A", random.Next(0, 101), Now);

            for (var operation = 0; operation < 100; operation++)
            {
                var quantity = random.Next(-5, 26);
                switch (random.Next(4))
                {
                    case 0:
                        item.TryReserve(quantity, Now.AddMinutes(operation));
                        break;
                    case 1:
                        item.TryCommit(quantity, Now.AddMinutes(operation));
                        break;
                    case 2:
                        item.TryRelease(quantity, Now.AddMinutes(operation));
                        break;
                    default:
                        if (quantity > 0)
                        {
                            item.Restock(quantity, Now.AddMinutes(operation));
                        }

                        break;
                }
            }

            return item.AvailableQuantity >= 0 && item.ReservedQuantity >= 0;
        }, iter: 2_000);
    }
}
