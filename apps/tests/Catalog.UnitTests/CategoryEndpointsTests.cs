using Catalog.Service.Endpoints;

namespace Catalog.UnitTests;

public sealed class CategoryEndpointsTests
{
    [Fact]
    public void ACompleteCreateRequestPassesValidation()
    {
        var request = new CreateCategoryRequest("electronics", "Eletrônicos");

        Assert.Null(CategoryEndpoints.ValidateCreateCategoryRequest(request));
    }

    [Theory]
    [InlineData("", "Eletrônicos")]
    [InlineData("   ", "Eletrônicos")]
    [InlineData("electronics", "")]
    [InlineData("electronics", "   ")]
    public void AMissingSlugOrNameFailsValidation(string slug, string name)
    {
        var request = new CreateCategoryRequest(slug, name);

        var errors = CategoryEndpoints.ValidateCreateCategoryRequest(request);

        Assert.NotNull(errors);
        Assert.Contains("request", errors.Keys);
    }
}
