using Catalog.Service.Data;
using Catalog.Service.Domain;
using MongoDB.Driver;

namespace Catalog.Service.Endpoints;

public sealed record CreateProductRequest(
    string Name,
    string Description,
    string CategorySlug,
    decimal Price,
    string Currency,
    string Sku,
    Dictionary<string, string>? Attributes,
    List<string>? Images);

public static class ProductEndpoints
{
    public static IEndpointRouteBuilder MapProductEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/products").WithTags("Products");

        group.MapGet("", ListAsync);
        group.MapGet("/by-ids", ListByIdsAsync);
        group.MapGet("/by-sku/{sku}", GetBySkuAsync);
        group.MapGet("/bestsellers", GetBestsellersAsync);
        group.MapGet("/{id}", GetByIdAsync);
        // Milestone 84: writes are catalog:admin-gated; every GET above stays open - browsing the catalog is not a privileged action.
        group.MapPost("", CreateAsync).RequireAuthorization("catalog:admin");

        return endpoints;
    }

    private static async Task<IResult> ListAsync(
        ProductRepository repository,
        string? category,
        int? skip,
        int? limit,
        CancellationToken cancellationToken)
    {
        var effectiveSkip = Math.Max(skip ?? 0, 0);
        var effectiveLimit = Math.Clamp(limit ?? 20, 1, 100);

        var products = await repository.ListAsync(category, effectiveSkip, effectiveLimit, cancellationToken);
        var total = await repository.CountAsync(category, cancellationToken);

        return Results.Ok(new
        {
            items = products,
            total,
            skip = effectiveSkip,
            limit = effectiveLimit
        });
    }

    private static async Task<IResult> ListByIdsAsync(
        ProductRepository repository,
        string ids,
        CancellationToken cancellationToken)
    {
        var idList = ids.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var products = await repository.FindByIdsAsync(idList, cancellationToken);
        return Results.Ok(products);
    }

    private static async Task<IResult> GetBySkuAsync(
        string sku,
        ProductRepository repository,
        CancellationToken cancellationToken)
    {
        var product = await repository.FindBySkuAsync(sku, cancellationToken);
        return product is null ? Results.NotFound() : Results.Ok(product);
    }

    private static async Task<IResult> GetBestsellersAsync(
        string? category,
        int? limit,
        BestsellersReader bestsellersReader,
        ProductRepository repository,
        CancellationToken cancellationToken)
    {
        var effectiveLimit = Math.Clamp(limit ?? 10, 1, 50);
        var ranked = await bestsellersReader.GetTopAsync(category, effectiveLimit, cancellationToken);

        // Rank order comes from Redis, not from any query MongoDB can
        // express - fetch each product individually (the list is at most
        // 50 long) and reassemble in the Redis-given order rather than
        // letting a batch Mongo query silently reorder them.
        var items = new List<object>(ranked.Count);
        foreach (var entry in ranked)
        {
            var product = await repository.FindBySkuAsync(entry.Sku, cancellationToken);
            if (product is not null)
            {
                items.Add(new { product, unitsSold = entry.UnitsSold });
            }
        }

        return Results.Ok(new { items, category });
    }

    private static async Task<IResult> GetByIdAsync(
        string id,
        ProductRepository repository,
        CancellationToken cancellationToken)
    {
        var product = await repository.FindByIdAsync(id, cancellationToken);
        return product is null ? Results.NotFound() : Results.Ok(product);
    }

    private static async Task<IResult> CreateAsync(
        CreateProductRequest request,
        ProductRepository repository,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.Name)
            || string.IsNullOrWhiteSpace(request.CategorySlug)
            || string.IsNullOrWhiteSpace(request.Sku)
            || request.Price <= 0)
        {
            return Results.ValidationProblem(new Dictionary<string, string[]>
            {
                ["request"] = ["name, categorySlug, sku are required and price must be positive."]
            });
        }

        var product = new Product
        {
            Name = request.Name,
            Description = request.Description,
            CategorySlug = request.CategorySlug,
            Price = request.Price,
            Currency = string.IsNullOrWhiteSpace(request.Currency) ? "BRL" : request.Currency,
            Sku = request.Sku,
            Attributes = request.Attributes ?? [],
            Images = request.Images ?? [],
            CreatedAt = DateTimeOffset.UtcNow
        };

        try
        {
            await repository.InsertAsync(product, cancellationToken);
        }
        catch (MongoWriteException ex) when (ex.WriteError?.Category == ServerErrorCategory.DuplicateKey)
        {
            return Results.Conflict(new { message = $"A product with sku '{request.Sku}' already exists." });
        }

        return Results.Created($"/products/{product.Id}", product);
    }
}
