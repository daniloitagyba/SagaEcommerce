using Catalog.Service.Endpoints;

namespace Catalog.UnitTests;

/// <summary>Catalog.Service has no owned Domain business rules beyond a plain Product/Category record - it owns the request-shaping rules at its HTTP boundary (pagination clamping, create-request validation), extracted to ProductEndpoints.NormalizeListQuery/NormalizeBestsellersLimit/ValidateCreateProductRequest so they're testable without a live Mongo.</summary>
public sealed class ProductEndpointsTests
{
    [Theory]
    [InlineData(null, null, 0, 20)]
    [InlineData(-5, null, 0, 20)]
    [InlineData(3, null, 3, 20)]
    [InlineData(null, 0, 0, 1)]
    [InlineData(null, -10, 0, 1)]
    [InlineData(null, 10_000, 0, 100)]
    [InlineData(null, 50, 0, 50)]
    public void ListQueryIsNormalizedIntoABoundedNonNegativePage(int? skip, int? limit, int expectedSkip, int expectedLimit)
    {
        var (normalizedSkip, normalizedLimit) = ProductEndpoints.NormalizeListQuery(skip, limit);

        Assert.Equal(expectedSkip, normalizedSkip);
        Assert.Equal(expectedLimit, normalizedLimit);
    }

    [Theory]
    [InlineData(null, 10)]
    [InlineData(0, 1)]
    [InlineData(-3, 1)]
    [InlineData(500, 50)]
    [InlineData(25, 25)]
    public void BestsellersLimitIsClampedToItsOwnRange(int? limit, int expectedLimit)
    {
        Assert.Equal(expectedLimit, ProductEndpoints.NormalizeBestsellersLimit(limit));
    }

    [Fact]
    public void ACompleteCreateRequestPassesValidation()
    {
        var request = new CreateProductRequest("Notebook", "desc", "electronics", 100m, "BRL", "SKU-1", null, null);

        Assert.Null(ProductEndpoints.ValidateCreateProductRequest(request));
    }

    [Theory]
    [InlineData("", "electronics", "SKU-1", 100)]
    [InlineData("   ", "electronics", "SKU-1", 100)]
    [InlineData("Notebook", "", "SKU-1", 100)]
    [InlineData("Notebook", "electronics", "", 100)]
    [InlineData("Notebook", "electronics", "SKU-1", 0)]
    [InlineData("Notebook", "electronics", "SKU-1", -1)]
    public void AnIncompleteOrInvalidCreateRequestFailsValidation(string name, string categorySlug, string sku, decimal price)
    {
        var request = new CreateProductRequest(name, "desc", categorySlug, price, "BRL", sku, null, null);

        var errors = ProductEndpoints.ValidateCreateProductRequest(request);

        Assert.NotNull(errors);
        Assert.Contains("request", errors.Keys);
    }
}
