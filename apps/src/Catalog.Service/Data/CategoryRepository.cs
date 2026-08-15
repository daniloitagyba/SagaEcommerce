using Catalog.Service.Domain;
using MongoDB.Driver;

namespace Catalog.Service.Data;

public sealed class CategoryRepository
{
    private readonly IMongoCollection<Category> _categories;

    public CategoryRepository(IMongoDatabase database)
    {
        _categories = database.GetCollection<Category>("categories");
    }

    public Task<List<Category>> ListAsync(CancellationToken cancellationToken)
    {
        return _categories.Find(Builders<Category>.Filter.Empty)
            .SortBy(category => category.Name)
            .ToListAsync(cancellationToken);
    }

    public Task<Category?> FindBySlugAsync(string slug, CancellationToken cancellationToken)
    {
        return _categories.Find(category => category.Slug == slug).FirstOrDefaultAsync(cancellationToken)!;
    }

    public Task InsertAsync(Category category, CancellationToken cancellationToken)
    {
        return _categories.InsertOneAsync(category, cancellationToken: cancellationToken);
    }

    public async Task<bool> UpdateNameAsync(string slug, string name, CancellationToken cancellationToken)
    {
        var result = await _categories.UpdateOneAsync(
            category => category.Slug == slug,
            Builders<Category>.Update.Set(category => category.Name, name),
            cancellationToken: cancellationToken);
        return result.MatchedCount > 0;
    }

    public async Task EnsureIndexesAsync(CancellationToken cancellationToken)
    {
        var slugIndex = new CreateIndexModel<Category>(
            Builders<Category>.IndexKeys.Ascending(category => category.Slug),
            new CreateIndexOptions { Unique = true });

        await _categories.Indexes.CreateOneAsync(slugIndex, cancellationToken: cancellationToken);
    }
}
