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

public sealed record UpdateProductRequest(
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
        group.MapPost("", CreateAsync).RequireAuthorization("catalog:admin");
        group.MapPut("/{id}", UpdateAsync).RequireAuthorization("catalog:admin");

        return endpoints;
    }

    /// <summary>Clamps skip/limit query params to safe, bounded values.</summary>
    internal static (int Skip, int Limit) NormalizeListQuery(int? skip, int? limit) =>
        (Math.Max(skip ?? 0, 0), Math.Clamp(limit ?? 20, 1, 100));

    private static async Task<IResult> ListAsync(
        ProductRepository repository,
        string? category,
        int? skip,
        int? limit,
        CancellationToken cancellationToken)
    {
        var (effectiveSkip, effectiveLimit) = NormalizeListQuery(skip, limit);

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

    /// <summary>Same ceiling as NormalizeListQuery's limit - an unbounded comma-separated id list turned this into an uncapped Mongo $in query.</summary>
    private const int MaxByIdsCount = 100;

    private static async Task<IResult> ListByIdsAsync(
        ProductRepository repository,
        string ids,
        CancellationToken cancellationToken)
    {
        var idList = ids.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Take(MaxByIdsCount)
            .ToArray();
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

    /// <summary>Not private: Catalog.UnitTests exercises this directly - see NormalizeListQuery.</summary>
    internal static int NormalizeBestsellersLimit(int? limit) => Math.Clamp(limit ?? 10, 1, 50);

    private static async Task<IResult> GetBestsellersAsync(
        string? category,
        int? limit,
        BestsellersReader bestsellersReader,
        ProductRepository repository,
        CancellationToken cancellationToken)
    {
        var effectiveLimit = NormalizeBestsellersLimit(limit);
        var ranked = await bestsellersReader.GetTopAsync(category, effectiveLimit, cancellationToken);

        var products = await repository.FindBySkusAsync(ranked.Select(entry => entry.Sku).ToArray(), cancellationToken);
        var productsBySku = products.ToDictionary(product => product.Sku, StringComparer.OrdinalIgnoreCase);

        var items = new List<object>(ranked.Count);
        foreach (var entry in ranked)
        {
            if (productsBySku.TryGetValue(entry.Sku, out var product))
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

    /// <summary>Validates a create-product request, returning field errors or null if valid.</summary>
    internal static IReadOnlyDictionary<string, string[]>? ValidateCreateProductRequest(CreateProductRequest request) =>
        string.IsNullOrWhiteSpace(request.Name)
            || string.IsNullOrWhiteSpace(request.CategorySlug)
            || string.IsNullOrWhiteSpace(request.Sku)
            || request.Price <= 0
            ? new Dictionary<string, string[]> { ["request"] = ["name, categorySlug, sku are required and price must be positive."] }
            : null;

    private static async Task<IResult> CreateAsync(
        CreateProductRequest request,
        ProductRepository repository,
        CategoryRepository categoryRepository,
        TimeProvider timeProvider,
        CancellationToken cancellationToken)
    {
        var validationErrors = ValidateCreateProductRequest(request);
        if (validationErrors is not null)
        {
            return Results.ValidationProblem(validationErrors);
        }

        var categoryExists = await categoryRepository.FindBySlugAsync(request.CategorySlug.Trim(), cancellationToken) is not null;
        if (!categoryExists)
        {
            return Results.ValidationProblem(new Dictionary<string, string[]>
            {
                ["categorySlug"] = [$"No category with slug '{request.CategorySlug}' exists."]
            });
        }

        Product product;
        try
        {
            product = Product.Create(
                request.Name,
                request.Description,
                request.CategorySlug,
                request.Price,
                request.Currency,
                request.Sku,
                request.Attributes,
                request.Images,
                timeProvider.GetUtcNow());
        }
        catch (ArgumentException exception)
        {
            return Results.ValidationProblem(new Dictionary<string, string[]> { ["request"] = [exception.Message] });
        }

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

    /// <summary>Same shape as ValidateCreateProductRequest - kept separate because Catalog.UnitTests exercises each independently and the two requests may diverge later (e.g. an update that can't change Sku).</summary>
    internal static IReadOnlyDictionary<string, string[]>? ValidateUpdateProductRequest(UpdateProductRequest request) =>
        string.IsNullOrWhiteSpace(request.Name)
            || string.IsNullOrWhiteSpace(request.CategorySlug)
            || string.IsNullOrWhiteSpace(request.Sku)
            || request.Price <= 0
            ? new Dictionary<string, string[]> { ["request"] = ["name, categorySlug, sku are required and price must be positive."] }
            : null;

    private static async Task<IResult> UpdateAsync(
        string id,
        UpdateProductRequest request,
        ProductRepository repository,
        CategoryRepository categoryRepository,
        CancellationToken cancellationToken)
    {
        var existing = await repository.FindByIdAsync(id, cancellationToken);
        if (existing is null)
        {
            return Results.NotFound();
        }

        var validationErrors = ValidateUpdateProductRequest(request);
        if (validationErrors is not null)
        {
            return Results.ValidationProblem(validationErrors);
        }

        var categoryExists = await categoryRepository.FindBySlugAsync(request.CategorySlug.Trim(), cancellationToken) is not null;
        if (!categoryExists)
        {
            return Results.ValidationProblem(new Dictionary<string, string[]>
            {
                ["categorySlug"] = [$"No category with slug '{request.CategorySlug}' exists."]
            });
        }

        try
        {
            existing.UpdateDetails(
                request.Name,
                request.Description,
                request.CategorySlug,
                request.Price,
                request.Currency,
                request.Sku,
                request.Attributes,
                request.Images);
        }
        catch (ArgumentException exception)
        {
            return Results.ValidationProblem(new Dictionary<string, string[]> { ["request"] = [exception.Message] });
        }

        try
        {
            await repository.UpdateAsync(existing, cancellationToken);
        }
        catch (MongoWriteException ex) when (ex.WriteError?.Category == ServerErrorCategory.DuplicateKey)
        {
            return Results.Conflict(new { message = $"A product with sku '{request.Sku}' already exists." });
        }

        return Results.Ok(existing);
    }
}
