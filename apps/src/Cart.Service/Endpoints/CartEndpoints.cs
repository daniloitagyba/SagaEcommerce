using BuildingBlocks;
using Cart.Service.Application;
using Cart.Service.Domain;

namespace Cart.Service.Endpoints;

public sealed record UpdateCartItemRequest(int Quantity);

public sealed record CartObservedDot(string ReplicaId, long Counter);

/// <summary>An offline cart operation to replay; Kind is "Increase" | "Decrease" | "Remove".</summary>
public sealed record CartMergeOperation(
    string Sku,
    string Kind,
    int Delta = 0,
    string? ProductName = null,
    decimal? UnitPrice = null,
    string? Currency = null,
    Guid OperationId = default,
    IReadOnlyList<CartObservedDot>? ObservedDots = null);

public sealed record CartMergeRequest(IReadOnlyList<CartMergeOperation>? Operations);

/// <summary>Cart endpoints; every route resolves to the caller's own cart via their authenticated identity.</summary>
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
        group.MapPost("/me/items/{sku}/refresh-price", RefreshItemPriceAsync);

        return endpoints;
    }

    private static async Task<IResult> GetCartAsync(
        HttpContext httpContext,
        ICartStore cartStore,
        CancellationToken cancellationToken)
    {
        var cartId = httpContext.GetCustomerId();
        var snapshot = await cartStore.GetSnapshotAsync(cartId, cancellationToken);

        return Results.Ok(ToCartResponse(snapshot));
    }

    private static async Task<IResult> PutItemAsync(
        string sku,
        UpdateCartItemRequest request,
        HttpContext httpContext,
        ICartStore cartStore,
        ICatalogClient catalogClient,
        TimeProvider timeProvider,
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
            item = existing.WithQuantity(request.Quantity);
        }
        else
        {
            var product = await catalogClient.FindBySkuAsync(sku, cancellationToken);
            if (product is null)
            {
                return Results.NotFound(new { message = $"No product with sku '{sku}' was found in the catalog." });
            }

            item = new CartLineItem(sku, request.Quantity, product.Price, product.Currency, product.Name, timeProvider.GetUtcNow());
        }

        await cartStore.UpsertItemAsync(cartId, item, cancellationToken);

        var snapshot = await cartStore.GetSnapshotAsync(cartId, cancellationToken);
        return Results.Ok(ToCartResponse(snapshot));
    }

    /// <summary>Re-reads the SKU's current price from the catalog and overwrites just this line's snapshot.</summary>
    private static async Task<IResult> RefreshItemPriceAsync(
        string sku,
        HttpContext httpContext,
        ICartStore cartStore,
        ICatalogClient catalogClient,
        TimeProvider timeProvider,
        CancellationToken cancellationToken)
    {
        var cartId = httpContext.GetCustomerId();
        var existing = await cartStore.GetItemAsync(cartId, sku, cancellationToken);
        if (existing is null)
        {
            return Results.NotFound(new { message = $"Sku '{sku}' is not in this cart." });
        }

        var product = await catalogClient.FindBySkuAsync(sku, cancellationToken);
        if (product is null)
        {
            return Results.NotFound(new { message = $"No product with sku '{sku}' was found in the catalog." });
        }

        var metadata = new CartItemMetadata(product.Name, product.Price, product.Currency, timeProvider.GetUtcNow());
        await cartStore.RefreshItemPriceAsync(cartId, sku, metadata, cancellationToken);

        var snapshot = await cartStore.GetSnapshotAsync(cartId, cancellationToken);
        return Results.Ok(ToCartResponse(snapshot));
    }

    private static async Task<IResult> DeleteItemAsync(
        string sku,
        HttpContext httpContext,
        ICartStore cartStore,
        CancellationToken cancellationToken)
    {
        var removed = await cartStore.RemoveItemAsync(httpContext.GetCustomerId(), sku, cancellationToken);
        return removed ? Results.NoContent() : Results.NotFound();
    }

    private static async Task<IResult> ClearCartAsync(
        string? cartId,
        long? expectedVersion,
        HttpContext httpContext,
        ICartStore cartStore,
        CancellationToken cancellationToken)
    {
        var ownerId = httpContext.GetCustomerId();
        if (cartId is null && expectedVersion is null)
        {
            await cartStore.ClearAsync(ownerId, cancellationToken);
            return Results.NoContent();
        }

        if (string.IsNullOrWhiteSpace(cartId) || expectedVersion is null || expectedVersion < 0)
        {
            return Results.ValidationProblem(new Dictionary<string, string[]>(StringComparer.Ordinal)
            {
                ["cart"] = ["cartId and a non-negative expectedVersion must be supplied together."]
            });
        }

        var cleared = await cartStore.ClearIfVersionAsync(
            ownerId, cartId, expectedVersion.Value, cancellationToken);
        return cleared
            ? Results.NoContent()
            : Results.Problem(
                detail: "The cart changed after checkout started and was preserved.",
                statusCode: StatusCodes.Status409Conflict,
                title: "Cart Changed");
    }

    /// <summary>Reconciles offline-tracked operations against the currently stored cart state.</summary>
    private static async Task<IResult> MergeAsync(
        CartMergeRequest request,
        HttpContext httpContext,
        ICartStore cartStore,
        TimeProvider timeProvider,
        CancellationToken cancellationToken)
    {
        var (clientState, errors) = BuildClientState(request.Operations, timeProvider);
        if (errors is not null)
        {
            return Results.ValidationProblem(errors);
        }

        var cartId = httpContext.GetCustomerId();
        await cartStore.MergeAsync(cartId, clientState!, cancellationToken);
        var snapshot = await cartStore.GetSnapshotAsync(cartId, cancellationToken);
        return Results.Ok(ToCartResponse(snapshot));
    }

    /// <summary>Validates and folds offline operations into a CRDT state, without touching the store.</summary>
    internal static (CartCrdtState? State, IReadOnlyDictionary<string, string[]>? Errors) BuildClientState(
        IReadOnlyList<CartMergeOperation>? operations,
        TimeProvider? timeProvider = null)
    {
        if (operations is null || operations.Count == 0)
        {
            return (null, new Dictionary<string, string[]>(StringComparer.Ordinal)
            {
                ["operations"] = ["At least one operation is required."]
            });
        }

        var clientState = CartCrdtState.Empty;
        var operationIds = new HashSet<Guid>();

        foreach (var operation in operations)
        {
            if (string.IsNullOrWhiteSpace(operation.Sku))
            {
                return (null, new Dictionary<string, string[]>(StringComparer.Ordinal)
                {
                    ["operations"] = ["Every operation must carry a sku."]
                });
            }

            if (operation.OperationId == Guid.Empty || !operationIds.Add(operation.OperationId))
            {
                return (null, new Dictionary<string, string[]>(StringComparer.Ordinal)
                {
                    ["operations"] = ["Every operation must carry a unique, non-empty operationId."]
                });
            }

            var replicaId = $"offline-operation:{operation.OperationId:N}";

            switch (operation.Kind)
            {
                case "Increase" when operation.Delta > 0 && operation.UnitPrice is not null && operation.Currency is not null && operation.ProductName is not null:
                    clientState = clientState.Increase(
                        operation.Sku, replicaId, operation.Delta, dotCounter: 1,
                        new CartItemMetadata(
                            operation.ProductName,
                            operation.UnitPrice.Value,
                            operation.Currency,
                            (timeProvider ?? TimeProvider.System).GetUtcNow()));
                    break;
                case "Decrease" when operation.Delta > 0:
                    clientState = clientState.Decrease(operation.Sku, replicaId, operation.Delta);
                    break;
                case "Remove" when operation.ObservedDots is { Count: > 0 and <= 256 }
                                   && operation.ObservedDots.All(dot =>
                                       !string.IsNullOrWhiteSpace(dot.ReplicaId)
                                       && dot.ReplicaId.Length <= 200
                                       && dot.Counter > 0):
                    clientState = clientState.RemoveObserved(
                        operation.Sku,
                        operation.ObservedDots.Select(dot => new CartDot(dot.ReplicaId, dot.Counter)));
                    break;
                default:
                    return (null, new Dictionary<string, string[]>(StringComparer.Ordinal)
                    {
                        ["operations"] = [$"Operation for sku '{operation.Sku}' has kind '{operation.Kind}' with missing or invalid arguments. Increase requires a positive delta, productName, unitPrice and currency; Decrease requires a positive delta; Remove requires between 1 and 256 valid observedDots from the cart snapshot."]
                    });
            }
        }

        return (clientState, null);
    }

    private static object ToCartResponse(CartSnapshot snapshot)
    {
        var items = snapshot.Items;
        var total = items.Sum(item => item.UnitPrice * item.Quantity);
        return new
        {
            cartId = snapshot.CartId,
            items,
            total,
            currency = items.Count > 0 ? items[0].Currency : "BRL",
            expiresInSeconds = snapshot.TimeToLive.HasValue ? (int)snapshot.TimeToLive.Value.TotalSeconds : (int?)null,
            version = snapshot.Version,
            causalContexts = snapshot.State.Items
                .Where(entry => entry.Value.IsPresent)
                .ToDictionary(
                    entry => entry.Key,
                    entry => entry.Value.LiveDots
                        .OrderBy(dot => dot.ReplicaId, StringComparer.Ordinal)
                        .ThenBy(dot => dot.Counter)
                        .Select(dot => new CartObservedDot(dot.ReplicaId, dot.Counter))
                        .ToArray(),
                    StringComparer.Ordinal)
        };
    }
}
