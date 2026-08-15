using Catalog.Service.Data;
using Catalog.Service.Domain;
using MongoDB.Driver;

namespace Catalog.Service.Endpoints;

public sealed record CreateCategoryRequest(string Slug, string Name);

public sealed record UpdateCategoryRequest(string Name);

public static class CategoryEndpoints
{
    public static IEndpointRouteBuilder MapCategoryEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/categories").WithTags("Categories");

        group.MapGet("", ListAsync);
        group.MapPost("", CreateAsync).RequireAuthorization("catalog:admin");
        group.MapPut("/{slug}", UpdateAsync).RequireAuthorization("catalog:admin");

        return endpoints;
    }

    private static async Task<IResult> ListAsync(CategoryRepository repository, CancellationToken cancellationToken)
    {
        var categories = await repository.ListAsync(cancellationToken);
        return Results.Ok(categories);
    }

    /// <summary>Not private: Catalog.UnitTests exercises this directly - see ProductEndpoints.ValidateCreateProductRequest.</summary>
    internal static IReadOnlyDictionary<string, string[]>? ValidateCreateCategoryRequest(CreateCategoryRequest request) =>
        string.IsNullOrWhiteSpace(request.Slug) || string.IsNullOrWhiteSpace(request.Name)
            ? new Dictionary<string, string[]> { ["request"] = ["slug and name are required."] }
            : null;

    private static async Task<IResult> CreateAsync(
        CreateCategoryRequest request,
        CategoryRepository repository,
        CancellationToken cancellationToken)
    {
        var validationErrors = ValidateCreateCategoryRequest(request);
        if (validationErrors is not null)
        {
            return Results.ValidationProblem(validationErrors);
        }

        var category = new Category { Id = Guid.NewGuid().ToString("N"), Slug = request.Slug.Trim(), Name = request.Name.Trim() };

        try
        {
            await repository.InsertAsync(category, cancellationToken);
        }
        catch (MongoWriteException ex) when (ex.WriteError?.Category == ServerErrorCategory.DuplicateKey)
        {
            return Results.Conflict(new { message = $"A category with slug '{request.Slug}' already exists." });
        }

        return Results.Created($"/categories/{category.Slug}", category);
    }

    /// <summary>Not private: Catalog.UnitTests exercises this directly - see ValidateCreateCategoryRequest.</summary>
    internal static IReadOnlyDictionary<string, string[]>? ValidateUpdateCategoryRequest(UpdateCategoryRequest request) =>
        string.IsNullOrWhiteSpace(request.Name)
            ? new Dictionary<string, string[]> { ["name"] = ["name is required."] }
            : null;

    private static async Task<IResult> UpdateAsync(
        string slug,
        UpdateCategoryRequest request,
        CategoryRepository repository,
        CancellationToken cancellationToken)
    {
        var validationErrors = ValidateUpdateCategoryRequest(request);
        if (validationErrors is not null)
        {
            return Results.ValidationProblem(validationErrors);
        }

        var updated = await repository.UpdateNameAsync(slug, request.Name.Trim(), cancellationToken);
        if (!updated)
        {
            return Results.NotFound();
        }

        return Results.Ok(await repository.FindBySlugAsync(slug, cancellationToken));
    }
}
