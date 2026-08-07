using Microsoft.Extensions.Options;
using NodaMoney;
using Orders.Application.Pricing;
using Orders.Domain;
using Orders.Domain.Pricing;

namespace Orders.UnitTests;

/// <summary>
/// Milestone 71: loyalty standing, and the geography that shipping and tax
/// actually depend on.
/// </summary>
public class CustomerTierTests
{
    private static readonly Currency Brl = Currency.FromCode("BRL");

    private static NRulesPricingEngine Engine(PricingOptions? options = null) =>
        new(Options.Create(options ?? new PricingOptions()));

    private static PricingLine Line(decimal unitPrice, int quantity = 1) =>
        new("SKU-BOOK-002", "Livro", "books", quantity, new Money(unitPrice, Brl));

    [Theory]
    [InlineData(0, CustomerTiers.Bronze)]
    [InlineData(999.99, CustomerTiers.Bronze)]
    [InlineData(1_000, CustomerTiers.Silver)]
    [InlineData(4_999.99, CustomerTiers.Silver)]
    [InlineData(5_000, CustomerTiers.Gold)]
    public void StandingIsEarnedFromLifetimeSpend(decimal lifetimeSpend, string expected)
    {
        Assert.Equal(expected, CustomerTiers.ForLifetimeSpend(lifetimeSpend));
    }

    [Fact]
    public void ACustomerClimbsThroughTheTiersAsTheySpend()
    {
        var customer = Customer.Create("customer-1", DateTimeOffset.UtcNow);
        Assert.Equal(CustomerTiers.Bronze, customer.Tier);

        Assert.False(customer.RecordCompletedOrder(600m));
        Assert.Equal(CustomerTiers.Bronze, customer.Tier);

        Assert.True(customer.RecordCompletedOrder(600m));
        Assert.Equal(CustomerTiers.Silver, customer.Tier);
        Assert.Equal(1_200m, customer.LifetimeSpend);
        Assert.Equal(2, customer.CompletedOrderCount);
    }

    [Fact]
    public void AFullRefundTakesBackTheSpendButNotTheStanding()
    {
        // Deliberate asymmetry: spend reverses, but tier doesn't - retroactively taking a discount away generates support tickets.
        var customer = Customer.Create("customer-1", DateTimeOffset.UtcNow);
        customer.RecordCompletedOrder(1_200m);
        Assert.Equal(CustomerTiers.Silver, customer.Tier);

        customer.ReverseCompletedOrder(1_200m);

        Assert.Equal(0m, customer.LifetimeSpend);
        Assert.Equal(0, customer.CompletedOrderCount);
        Assert.Equal(CustomerTiers.Silver, customer.Tier);
    }

    [Fact]
    public void TheWorkersTierThresholdsMatchTheDomains()
    {
        // Orders.Worker deliberately doesn't reference Orders.Domain, so its SQL thresholds are a second copy - this stops the two drifting.
        Assert.Equal(CustomerTiers.SilverThreshold, Orders.Worker.CustomerTierThresholds.Silver);
        Assert.Equal(CustomerTiers.GoldThreshold, Orders.Worker.CustomerTierThresholds.Gold);
        Assert.Equal(CustomerTiers.Bronze, Orders.Worker.CustomerTierThresholds.BronzeName);
        Assert.Equal(CustomerTiers.Silver, Orders.Worker.CustomerTierThresholds.SilverName);
        Assert.Equal(CustomerTiers.Gold, Orders.Worker.CustomerTierThresholds.GoldName);
    }

    [Fact]
    public void AGoldMemberGetsAStandingDiscountWithoutTypingAnything()
    {
        var gold = new PricingCustomer("customer-1", CustomerTiers.Gold, DateTimeOffset.UtcNow.AddYears(-2));
        var breakdown = Engine().Price(new PricingRequest("customer-1", Brl, [Line(100m)], Customer: gold));

        var discount = Assert.Single(breakdown.Discounts);
        Assert.Equal("TIER-GOLD", discount.Code);
        Assert.Equal(new Money(7m, Brl), discount.Amount);
    }

    [Fact]
    public void ABronzeMemberGetsNothingExtra()
    {
        var bronze = new PricingCustomer("customer-1", CustomerTiers.Bronze, DateTimeOffset.UtcNow);
        var breakdown = Engine().Price(new PricingRequest("customer-1", Brl, [Line(100m)], Customer: bronze));

        Assert.Empty(breakdown.Discounts);
    }

    [Fact]
    public void TheTierDiscountStacksWithACouponAndTheEngineStillCapsTheTotal()
    {
        // Five independent rules, none aware of the others - the cap keeps them from together exceeding the order.
        var options = new PricingOptions
        {
            CategoryDiscounts = new Dictionary<string, decimal>(StringComparer.OrdinalIgnoreCase) { ["books"] = 80m },
            FlatShippingAmount = 0m
        };
        var gold = new PricingCustomer("customer-1", CustomerTiers.Gold, DateTimeOffset.UtcNow.AddYears(-2));
        var coupon = new ResolvedCoupon("HALFOFF", "50% off", 50m);

        var breakdown = Engine(options).Price(
            new PricingRequest("customer-1", Brl, [Line(100m)], coupon, gold));

        // 50 + 80 + 7 = 137% asked for; 100% applied.
        Assert.Equal(new Money(100m, Brl), breakdown.DiscountTotal);
        Assert.Equal(new Money(0m, Brl), breakdown.GrandTotal);
    }

    [Theory]
    [InlineData("01310-100", "SP", 14.90)]
    [InlineData("22041-001", "RJ", 19.90)]
    [InlineData("66000-000", "PA", 49.90)]
    [InlineData("99999-999", "XX", 34.90)]  // outside every known zone
    public void ShippingFollowsTheDestination(string postalCode, string region, decimal expected)
    {
        var address = new ShippingAddress("Rua A, 1", "Cidade", region, postalCode);
        var destination = new PricingDestination(address.Region, address.PostalPrefix);

        // Below the free-shipping threshold so the zone cost actually shows.
        var breakdown = Engine(new PricingOptions { TaxRatePercentage = 0m })
            .Price(new PricingRequest("customer-1", Brl, [Line(50m)], Destination: destination));

        Assert.Equal(new Money(expected, Brl), breakdown.ShippingTotal);
    }

    [Fact]
    public void TaxFollowsWhereTheGoodsLandNotWhereTheShopIs()
    {
        var sp = new PricingDestination("SP", "01");
        var rj = new PricingDestination("RJ", "22");

        var inSaoPaulo = Engine().Price(new PricingRequest("customer-1", Brl, [Line(1_000m)], Destination: sp));
        var inRio = Engine().Price(new PricingRequest("customer-1", Brl, [Line(1_000m)], Destination: rj));

        Assert.Equal(new Money(180m, Brl), inSaoPaulo.TaxTotal);  // 18%
        Assert.Equal(new Money(200m, Brl), inRio.TaxTotal);       // 20%
    }

    [Fact]
    public void AnOrderWithNoAddressStillPricesOnTheOldFlatTerms()
    {
        // The amount-only checkout shape has no destination and still has to work.
        var breakdown = Engine().Price(new PricingRequest("customer-1", Brl, [Line(50m)]));

        Assert.Equal(new Money(19.90m, Brl), breakdown.ShippingTotal);
        Assert.Equal(new Money(0m, Brl), breakdown.TaxTotal);
    }

    [Fact]
    public void AnIncompleteAddressIsTreatedAsNoAddress()
    {
        Assert.False(new ShippingAddress("", "Cidade", "SP", "01310-100").IsComplete);
        Assert.False(new ShippingAddress("Rua A", "Cidade", "SP", "x").IsComplete);
        Assert.True(new ShippingAddress("Rua A", "Cidade", "SP", "01310-100").IsComplete);
        Assert.Equal("01", new ShippingAddress("Rua A", "Cidade", "SP", "01310-100").PostalPrefix);
    }
}
