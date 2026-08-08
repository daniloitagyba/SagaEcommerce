using Catalog.Service.Data;
using Catalog.Service.Domain;

namespace Catalog.Service.Endpoints;

public sealed record CreateCategoryRequest(string Slug, string Name);

public static class CategoryEndpoints
{
    public static IEndpointRouteBuilder MapCategoryEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/categories").WithTags("Categories");

        group.MapGet("", ListAsync);
        group.MapPost("", CreateAsync).RequireAuthorization("catalog:admin");

        return endpoints;
    }

    private static async Task<IResult> ListAsync(CategoryRepository repository, CancellationToken cancellationToken)
    {
        var categories = await repository.ListAsync(cancellationToken);
        return Results.Ok(categories);
    }

    private static async Task<IResult> CreateAsync(
        CreateCategoryRequest request,
        CategoryRepository repository,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.Slug) || string.IsNullOrWhiteSpace(request.Name))
        {
            return Results.ValidationProblem(new Dictionary<string, string[]>
            {
                ["request"] = ["slug and name are required."]
            });
        }

        var category = new Category { Id = Guid.NewGuid().ToString("N"), Slug = request.Slug, Name = request.Name };
        await repository.InsertAsync(category, cancellationToken);
        return Results.Created($"/categories/{category.Slug}", category);
    }
}
