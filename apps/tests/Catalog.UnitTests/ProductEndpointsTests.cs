using Catalog.Service.Endpoints;

namespace Catalog.UnitTests;

/// <summary>
/// Catalog.Service has no owned Domain business rules beyond a plain
/// Product/Category record (see docs/architecture/milestone-61...) - what
/// it does own is the request-shaping rules at its HTTP boundary: how a
/// caller's pagination request gets clamped before it becomes a Mongo
/// query, and what makes a create request valid. Both were previously
/// inline in the endpoint lambdas with zero test coverage; extracted to
/// ProductEndpoints.NormalizeListQuery/NormalizeBestsellersLimit/
/// ValidateCreateProductRequest so they're testable without a live Mongo.
/// </summary>
public sealed class ProductEndpointsTests
{
    [Theory]
    [InlineData(null, null, 0, 20)]
    // A caller can't page backwards into a negative offset.
    [InlineData(-5, null, 0, 20)]
    [InlineData(3, null, 3, 20)]
    // limit=0 (or negative) would ask Mongo for nothing; the floor keeps a page non-empty.
    [InlineData(null, 0, 0, 1)]
    [InlineData(null, -10, 0, 1)]
    // An unbounded limit would let one caller pull the entire catalog in one request.
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
    // Bestsellers has its own, tighter ceiling than the general product list.
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
    // A free or negative price is never a real product.
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
