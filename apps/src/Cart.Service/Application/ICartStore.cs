using Cart.Service.Domain;

namespace Cart.Service.Application;

public interface ICartStore
{
    Task<CartSnapshot> GetSnapshotAsync(string ownerId, CancellationToken cancellationToken);

    Task<CartLineItem?> GetItemAsync(string cartId, string sku, CancellationToken cancellationToken);

    Task UpsertItemAsync(string ownerId, CartLineItem item, CancellationToken cancellationToken);

    Task<bool> RefreshItemPriceAsync(string ownerId, string sku, CartItemMetadata metadata, CancellationToken cancellationToken);

    Task<bool> RemoveItemAsync(string ownerId, string sku, CancellationToken cancellationToken);

    Task<bool> ClearAsync(string cartId, CancellationToken cancellationToken);

    Task<bool> ClearIfVersionAsync(string ownerId, string cartId, long expectedVersion, CancellationToken cancellationToken);

    Task<IReadOnlyList<CartLineItem>> MergeAsync(string cartId, CartCrdtState clientState, CancellationToken cancellationToken);
}
