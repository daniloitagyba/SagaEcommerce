using Catalog.Service.Domain;

namespace Catalog.UnitTests;

public sealed class ProductDomainTests
{
    [Fact]
    public void FactoryNormalizesIdentityAndCurrency()
    {
        var product = Product.Create(
            " Notebook ",
            " Description ",
            " electronics ",
            100m,
            "brl",
            " SKU-1 ",
            null,
            null,
            DateTimeOffset.UnixEpoch);

        Assert.Equal("Notebook", product.Name);
        Assert.Equal("electronics", product.CategorySlug);
        Assert.Equal("BRL", product.Currency);
        Assert.Equal("SKU-1", product.Sku);
    }

    [Fact]
    public void FactoryRejectsANonPositivePrice()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => Product.Create(
            "Notebook", "Description", "electronics", 0m, "BRL", "SKU-1", null, null, DateTimeOffset.UnixEpoch));
    }
}
