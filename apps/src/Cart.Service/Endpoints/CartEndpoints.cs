using BuildingBlocks;
using Cart.Service.Data;
using Cart.Service.Domain;

namespace Cart.Service.Endpoints;

public sealed record UpdateCartItemRequest(int Quantity);

public static class CartEndpoints
{
    public static IEndpointRouteBuilder MapCartEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/carts").WithTags("Cart");

        group.MapGet("/{cartId}", GetCartAsync);
        group.MapPut("/{cartId}/items/{sku}", PutItemAsync);
        group.MapDelete("/{cartId}/items/{sku}", DeleteItemAsync);
        group.MapDelete("/{cartId}", ClearCartAsync);

        return endpoints;
    }

    private static async Task<IResult> GetCartAsync(
        string cartId,
        CartStore cartStore,
        CancellationToken cancellationToken)
    {
        var items = await cartStore.GetAsync(cartId, cancellationToken);
        var ttl = await cartStore.GetTimeToLiveAsync(cartId, cancellationToken);

        return Results.Ok(ToCartResponse(items, ttl));
    }

    private static async Task<IResult> PutItemAsync(
        string cartId,
        string sku,
        UpdateCartItemRequest request,
        CartStore cartStore,
        ICatalogClient catalogClient,
        CancellationToken cancellationToken)
    {
        if (request.Quantity <= 0)
        {
            return Results.ValidationProblem(new Dictionary<string, string[]>
            {
                ["quantity"] = ["Quantity must be positive; use DELETE to remove an item."]
            });
        }

        var existing = await cartStore.GetItemAsync(cartId, sku, cancellationToken);
        CartLineItem item;

        if (existing is not null)
        {
            // Quantity change on a SKU already in the cart: no Catalog call,
            // no re-snapshotting of price/name - see CartLineItem's comment.
            item = existing.WithQuantity(request.Quantity);
        }
        else
        {
            var product = await catalogClient.FindBySkuAsync(sku, cancellationToken);
            if (product is null)
            {
                return Results.NotFound(new { message = $"No product with sku '{sku}' was found in the catalog." });
            }

            item = new CartLineItem(sku, request.Quantity, product.Price, product.Currency, product.Name, DateTimeOffset.UtcNow);
        }

        await cartStore.UpsertItemAsync(cartId, item, cancellationToken);

        var items = await cartStore.GetAsync(cartId, cancellationToken);
        var ttl = await cartStore.GetTimeToLiveAsync(cartId, cancellationToken);
        return Results.Ok(ToCartResponse(items, ttl));
    }

    private static async Task<IResult> DeleteItemAsync(
        string cartId,
        string sku,
        CartStore cartStore,
        CancellationToken cancellationToken)
    {
        var removed = await cartStore.RemoveItemAsync(cartId, sku, cancellationToken);
        return removed ? Results.NoContent() : Results.NotFound();
    }

    private static async Task<IResult> ClearCartAsync(
        string cartId,
        CartStore cartStore,
        CancellationToken cancellationToken)
    {
        await cartStore.ClearAsync(cartId, cancellationToken);
        return Results.NoContent();
    }

    private static object ToCartResponse(IReadOnlyList<CartLineItem> items, TimeSpan? ttl)
    {
        var total = items.Sum(item => item.UnitPrice * item.Quantity);
        return new
        {
            items,
            total,
            currency = items.Count > 0 ? items[0].Currency : "BRL",
            expiresInSeconds = ttl.HasValue ? (int)ttl.Value.TotalSeconds : (int?)null
        };
    }
}
