using BuildingBlocks;
using Cart.Service.Data;
using Cart.Service.Domain;

namespace Cart.Service.Endpoints;

public sealed record UpdateCartItemRequest(int Quantity);

/// <summary>
/// Milestone 86: one operation a client tracked while it couldn't reach
/// this service - a different tab, a device that went offline - replayed
/// here rather than shipped as raw CRDT dots, which are an implementation
/// detail this wire contract has no reason to expose. Kind is
/// "Increase" | "Decrease" | "Remove"; the server mints its own dot for
/// each Increase (CartStore.MergeAsync), since only the server's own
/// operations need to be causally distinguishable from each other here -
/// see CartCrdtState's class comment for why per-key CRDT composition
/// makes this sufficient without a client-supplied clock.
/// </summary>
public sealed record CartMergeOperation(string Sku, string Kind, int Delta = 0, string? ProductName = null, decimal? UnitPrice = null, string? Currency = null);

public sealed record CartMergeRequest(IReadOnlyList<CartMergeOperation>? Operations);

/// <summary>
/// Milestone 84: every route resolves to the caller's own cart - there is
/// no cartId left in the URL to name someone else's. Before this
/// milestone, cartId was a client-supplied opaque string with no owner
/// check at all: anyone who could guess or enumerate one could read or
/// clear another shopper's cart. Deriving the storage key from the
/// authenticated caller's own identity removes that surface rather than
/// guarding it - there is nothing left to enumerate.
/// </summary>
public static class CartEndpoints
{
    public static IEndpointRouteBuilder MapCartEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/carts").WithTags("Cart").RequireAuthorization();

        group.MapGet("/me", GetCartAsync);
        group.MapPut("/me/items/{sku}", PutItemAsync);
        group.MapDelete("/me/items/{sku}", DeleteItemAsync);
        group.MapDelete("/me", ClearCartAsync);
        group.MapPost("/me/merge", MergeAsync);

        return endpoints;
    }

    private static async Task<IResult> GetCartAsync(
        HttpContext httpContext,
        CartStore cartStore,
        CancellationToken cancellationToken)
    {
        var cartId = httpContext.GetCustomerId();
        var items = await cartStore.GetAsync(cartId, cancellationToken);
        var ttl = await cartStore.GetTimeToLiveAsync(cartId, cancellationToken);
        var version = await cartStore.GetVersionAsync(cartId, cancellationToken);

        return Results.Ok(ToCartResponse(items, ttl, version));
    }

    private static async Task<IResult> PutItemAsync(
        string sku,
        UpdateCartItemRequest request,
        HttpContext httpContext,
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

        var cartId = httpContext.GetCustomerId();
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
        var version = await cartStore.GetVersionAsync(cartId, cancellationToken);
        return Results.Ok(ToCartResponse(items, ttl, version));
    }

    private static async Task<IResult> DeleteItemAsync(
        string sku,
        HttpContext httpContext,
        CartStore cartStore,
        CancellationToken cancellationToken)
    {
        var removed = await cartStore.RemoveItemAsync(httpContext.GetCustomerId(), sku, cancellationToken);
        return removed ? Results.NoContent() : Results.NotFound();
    }

    private static async Task<IResult> ClearCartAsync(
        HttpContext httpContext,
        CartStore cartStore,
        CancellationToken cancellationToken)
    {
        await cartStore.ClearAsync(httpContext.GetCustomerId(), cancellationToken);
        return Results.NoContent();
    }

    /// <summary>
    /// Milestone 86: reconciles what a client tracked while it couldn't
    /// reach this cart against whatever is currently stored - see
    /// CartCrdtState.Merge and CartStore.MergeAsync for the actual CRDT
    /// join. Every real operation still goes through PutItemAsync/
    /// DeleteItemAsync above unchanged; this route exists specifically for
    /// the divergent-then-reconciled case those two were never meant to solve.
    /// </summary>
    private static async Task<IResult> MergeAsync(
        CartMergeRequest request,
        HttpContext httpContext,
        CartStore cartStore,
        CancellationToken cancellationToken)
    {
        if (request.Operations is null || request.Operations.Count == 0)
        {
            return Results.ValidationProblem(new Dictionary<string, string[]>(StringComparer.Ordinal)
            {
                ["operations"] = ["At least one operation is required."]
            });
        }

        var cartId = httpContext.GetCustomerId();
        var clientState = CartCrdtState.Empty;
        var dotCounter = DateTimeOffset.UtcNow.Ticks;

        foreach (var operation in request.Operations)
        {
            if (string.IsNullOrWhiteSpace(operation.Sku))
            {
                return Results.ValidationProblem(new Dictionary<string, string[]>(StringComparer.Ordinal)
                {
                    ["operations"] = ["Every operation must carry a sku."]
                });
            }

            switch (operation.Kind)
            {
                case "Increase" when operation.Delta > 0 && operation.UnitPrice is not null && operation.Currency is not null && operation.ProductName is not null:
                    clientState = clientState.Increase(
                        operation.Sku, "offline-client", operation.Delta, dotCounter++,
                        new CartItemMetadata(operation.ProductName, operation.UnitPrice.Value, operation.Currency, DateTimeOffset.UtcNow));
                    break;
                case "Decrease" when operation.Delta > 0:
                    clientState = clientState.Decrease(operation.Sku, "offline-client", operation.Delta);
                    break;
                case "Remove":
                    clientState = clientState.Remove(operation.Sku);
                    break;
                default:
                    return Results.ValidationProblem(new Dictionary<string, string[]>(StringComparer.Ordinal)
                    {
                        ["operations"] = [$"Operation for sku '{operation.Sku}' has kind '{operation.Kind}' with missing or invalid arguments. Increase requires a positive delta, productName, unitPrice and currency; Decrease requires a positive delta; Remove requires neither."]
                    });
            }
        }

        var merged = await cartStore.MergeAsync(cartId, clientState, cancellationToken);
        var ttl = await cartStore.GetTimeToLiveAsync(cartId, cancellationToken);
        var version = await cartStore.GetVersionAsync(cartId, cancellationToken);
        return Results.Ok(ToCartResponse(merged, ttl, version));
    }

    private static object ToCartResponse(IReadOnlyList<CartLineItem> items, TimeSpan? ttl, long version)
    {
        var total = items.Sum(item => item.UnitPrice * item.Quantity);
        return new
        {
            items,
            total,
            currency = items.Count > 0 ? items[0].Currency : "BRL",
            expiresInSeconds = ttl.HasValue ? (int)ttl.Value.TotalSeconds : (int?)null,
            version
        };
    }
}
