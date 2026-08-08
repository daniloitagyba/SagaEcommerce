using NodaMoney;
using Orders.Domain.Pricing;

namespace Orders.UnitTests;

/// <summary>
/// Milestone 82: AllocateTax in isolation, with hand-picked per-line
/// discounts rather than ones a real promotion would produce - the pricing
/// engine's own AllocateDiscounts always spreads a discount proportional to
/// raw subtotal (see PricingEngineTests), which makes discounted-value and
/// raw-subtotal weighting numerically identical for anything the real
/// engine can produce. Feeding AllocateTax an uneven discount directly is
/// the only way to actually observe it weighting by discounted value.
/// </summary>
public class PricingAllocationTests
{
    private static readonly Currency Brl = Currency.FromCode("BRL");

    private static PricingLine Line(string sku, decimal unitPrice) =>
        new(sku, $"Product {sku}", "misc", 1, new Money(unitPrice, Brl));

    [Fact]
    public void WeightsByDiscountedValueNotRawSubtotal()
    {
        // Two equal 100.00 lines, but line A carries the entire 40.00
        // discount and line B carries none - discounted values are 60.00
        // and 100.00, an uneven 3:5 split despite equal raw subtotals.
        var lines = new[] { Line("SKU-A", 100m), Line("SKU-B", 100m) };
        var lineDiscounts = new[] { new Money(40m, Brl), new Money(0m, Brl) };

        var lineTaxes = PricingAllocation.AllocateTax(new Money(16m, Brl), lines, lineDiscounts, Brl);

        Assert.Equal(new Money(6m, Brl), lineTaxes[0]);
        Assert.Equal(new Money(10m, Brl), lineTaxes[1]);
    }

    [Fact]
    public void SumsExactlyToTheTaxTotalRegardlessOfRounding()
    {
        var lines = new[] { Line("SKU-A", 33.33m), Line("SKU-B", 66.67m), Line("SKU-C", 10m) };
        var lineDiscounts = new[] { new Money(1.11m, Brl), new Money(2.22m, Brl), new Money(0m, Brl) };
        var taxTotal = new Money(7.77m, Brl);

        var lineTaxes = PricingAllocation.AllocateTax(taxTotal, lines, lineDiscounts, Brl);

        Assert.Equal(taxTotal, lineTaxes.Aggregate(new Money(0m, Brl), (running, tax) => running + tax));
        Assert.All(lineTaxes, tax => Assert.True(tax.Amount >= 0m));
    }

    [Fact]
    public void ZeroTaxAllocatesZeroToEveryLine()
    {
        var lines = new[] { Line("SKU-A", 100m), Line("SKU-B", 50m) };
        var lineDiscounts = new[] { new Money(0m, Brl), new Money(0m, Brl) };

        var lineTaxes = PricingAllocation.AllocateTax(new Money(0m, Brl), lines, lineDiscounts, Brl);

        Assert.All(lineTaxes, tax => Assert.Equal(new Money(0m, Brl), tax));
    }

    [Fact]
    public void NoLinesAllocatesNothing()
    {
        Assert.Empty(PricingAllocation.AllocateTax(new Money(10m, Brl), [], [], Brl));
    }
}
