using Catalog.Service.Data;
using Catalog.Service.Domain;
using MongoDB.Driver;
using Testcontainers.MongoDb;

namespace Catalog.IntegrationTests;

public sealed class ProductRepositoryTests : IAsyncLifetime
{
    private MongoDbContainer _mongo = null!;
    private ProductRepository _repository = null!;

    public async Task InitializeAsync()
    {
        _mongo = new MongoDbBuilder("mongo:8.0").Build();
        await _mongo.StartAsync();

        var client = new MongoClient(_mongo.GetConnectionString());
        var database = client.GetDatabase("catalog_test");
        _repository = new ProductRepository(database);
        await _repository.EnsureIndexesAsync(CancellationToken.None);
    }

    public async Task DisposeAsync()
    {
        await _mongo.DisposeAsync();
    }

    [Fact]
    public async Task InsertedProductCanBeFoundByIdAndListedByCategory()
    {
        var product = Product.Create(
            "Test Notebook",
            "A notebook for testing.",
            "electronics",
            1999.90m,
            "BRL",
            $"SKU-TEST-{Guid.NewGuid():N}",
            new Dictionary<string, string> { ["ram"] = "8GB" },
            images: null,
            createdAt: DateTimeOffset.UtcNow);

        await _repository.InsertAsync(product, CancellationToken.None);

        var fetched = await _repository.FindByIdAsync(product.Id, CancellationToken.None);
        var listed = await _repository.ListAsync("electronics", skip: 0, limit: 10, CancellationToken.None);
        var count = await _repository.CountAsync("electronics", CancellationToken.None);
        var otherCategory = await _repository.ListAsync("books", skip: 0, limit: 10, CancellationToken.None);

        Assert.NotNull(fetched);
        Assert.Equal(product.Sku, fetched!.Sku);
        Assert.Equal("8GB", fetched.Attributes["ram"]);
        Assert.Contains(listed, item => item.Id == product.Id);
        Assert.Equal(1, count);
        Assert.DoesNotContain(otherCategory, item => item.Id == product.Id);
    }

    [Fact]
    public async Task FindBySkuAsyncFindsTheProductAndReturnsNullForAnUnknownSku()
    {
        var sku = $"SKU-BY-SKU-{Guid.NewGuid():N}";
        var product = Product.Create(
            "Findable by SKU", string.Empty, "electronics", 10m, "BRL", sku,
            attributes: null, images: null, createdAt: DateTimeOffset.UtcNow);
        await _repository.InsertAsync(product, CancellationToken.None);

        var found = await _repository.FindBySkuAsync(sku, CancellationToken.None);
        var notFound = await _repository.FindBySkuAsync("does-not-exist", CancellationToken.None);

        Assert.NotNull(found);
        Assert.Equal(product.Id, found!.Id);
        Assert.Null(notFound);
    }

    [Fact]
    public async Task FindByIdAsyncReturnsNullForAMalformedId()
    {
        var result = await _repository.FindByIdAsync("not-a-valid-object-id", CancellationToken.None);

        Assert.Null(result);
    }

    [Fact]
    public async Task DuplicateSkuIsRejectedByTheUniqueIndex()
    {
        var sku = $"SKU-DUP-{Guid.NewGuid():N}";
        var first = Product.Create(
            "First", string.Empty, "books", 10m, "BRL", sku,
            attributes: null, images: null, createdAt: DateTimeOffset.UtcNow);
        var second = Product.Create(
            "Second", string.Empty, "books", 20m, "BRL", sku,
            attributes: null, images: null, createdAt: DateTimeOffset.UtcNow);

        await _repository.InsertAsync(first, CancellationToken.None);

        await Assert.ThrowsAsync<MongoWriteException>(() => _repository.InsertAsync(second, CancellationToken.None));
    }
}
