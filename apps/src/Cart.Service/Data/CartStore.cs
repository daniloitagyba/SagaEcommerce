using System.Text.Json;
using Cart.Service.Domain;
using Microsoft.Extensions.Options;
using Polly;
using Polly.Registry;
using StackExchange.Redis;

namespace Cart.Service.Data;

/// <summary>
/// Redis IS the system of record here, not a cache in front of one - no
/// Postgres fallback, no cache-aside factory like RedisOrderCache. If lost,
/// the cart is simply gone, an acceptable trade for ephemeral, low-value
/// state. A cart is a single Redis Hash (field = Sku, value = JSON) so it
/// reads and refreshes its TTL in one round trip.
///
/// Deliberately does NOT use BuildingBlocks' shared "redis" pipeline: its
/// 150ms timeout is tuned for cache-aside use, where a timeout just means
/// "fall back to Postgres" - fast failure is wrong when there's no
/// fallback, and the same aggressive timeout failed otherwise-successful
/// requests against a cold Testcontainers Redis on a loaded CI runner.
/// CartResiliencePipeline keeps the circuit breaker but uses a longer timeout.
/// </summary>
public sealed class CartStore(
    IConnectionMultiplexer connectionMultiplexer,
    ResiliencePipelineProvider<string> pipelineProvider,
    IOptions<CartOptions> options)
{
    public const string ResiliencePipelineName = "cart-redis";

    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);
    private readonly CartOptions _options = options.Value;
    private readonly ResiliencePipeline _pipeline = pipelineProvider.GetPipeline(ResiliencePipelineName);

    public Task<IReadOnlyList<CartLineItem>> GetAsync(string cartId, CancellationToken cancellationToken)
    {
        return _pipeline.ExecuteAsync(async ct =>
        {
            var database = connectionMultiplexer.GetDatabase();
            var entries = await database.HashGetAllAsync(CartKey(cartId)).WaitAsync(ct);
            return (IReadOnlyList<CartLineItem>)entries
                // __version shares the hash with real line items (see GetVersionAsync) - not a Sku, skipped here.
                .Where(entry => entry.Name != VersionField)
                .Select(entry => Deserialize(entry.Value!))
                .OrderBy(item => item.AddedAt)
                .ToList();
        }, cancellationToken).AsTask();
    }

    public Task<CartLineItem?> GetItemAsync(string cartId, string sku, CancellationToken cancellationToken)
    {
        return _pipeline.ExecuteAsync(async ct =>
        {
            var database = connectionMultiplexer.GetDatabase();
            var value = await database.HashGetAsync(CartKey(cartId), sku).WaitAsync(ct);
            return value.HasValue ? Deserialize(value!) : null;
        }, cancellationToken).AsTask();
    }

    public Task UpsertItemAsync(string cartId, CartLineItem item, CancellationToken cancellationToken)
    {
        return _pipeline.ExecuteAsync(async ct =>
        {
            var database = connectionMultiplexer.GetDatabase();
            var key = CartKey(cartId);
            var payload = JsonSerializer.Serialize(item, SerializerOptions);
            await database.HashSetAsync(key, item.Sku, payload).WaitAsync(ct);
            await database.HashIncrementAsync(key, VersionField).WaitAsync(ct);
            await database.KeyExpireAsync(key, TimeSpan.FromSeconds(_options.TimeToLiveSeconds)).WaitAsync(ct);
        }, cancellationToken).AsTask();
    }

    public Task<bool> RemoveItemAsync(string cartId, string sku, CancellationToken cancellationToken)
    {
        return _pipeline.ExecuteAsync(async ct =>
        {
            var database = connectionMultiplexer.GetDatabase();
            var key = CartKey(cartId);
            var removed = await database.HashDeleteAsync(key, sku).WaitAsync(ct);
            if (removed)
            {
                await database.HashIncrementAsync(key, VersionField).WaitAsync(ct);
            }

            // > 1, not > 0: the version field itself is a hash entry, so an
            // otherwise-empty cart still has one field left after the item
            // above is gone.
            if (removed && await database.HashLengthAsync(key).WaitAsync(ct) > 1)
            {
                await database.KeyExpireAsync(key, TimeSpan.FromSeconds(_options.TimeToLiveSeconds)).WaitAsync(ct);
            }

            return removed;
        }, cancellationToken).AsTask();
    }

    public Task<bool> ClearAsync(string cartId, CancellationToken cancellationToken)
    {
        return _pipeline.ExecuteAsync(async ct =>
        {
            var database = connectionMultiplexer.GetDatabase();
            return await database.KeyDeleteAsync(CartKey(cartId)).WaitAsync(ct);
        }, cancellationToken).AsTask();
    }

    public Task<TimeSpan?> GetTimeToLiveAsync(string cartId, CancellationToken cancellationToken)
    {
        return _pipeline.ExecuteAsync(async ct =>
        {
            var database = connectionMultiplexer.GetDatabase();
            return await database.KeyTimeToLiveAsync(CartKey(cartId)).WaitAsync(ct);
        }, cancellationToken).AsTask();
    }

    /// <summary>
    /// Milestone 85: a monotonically increasing counter, bumped on every
    /// mutation - the BFF's checkout uses it to build a deterministic
    /// Idempotency-Key ("this exact cart state, checked out once") without
    /// needing a client-generated one, and to notice when the cart changed
    /// under a shopper mid-checkout. Stored as an ordinary hash field
    /// rather than a separate key so it lives and dies with the cart itself -
    /// no separate TTL to keep in sync, no orphaned counter after a cart expires.
    /// </summary>
    public Task<long> GetVersionAsync(string cartId, CancellationToken cancellationToken)
    {
        return _pipeline.ExecuteAsync(async ct =>
        {
            var database = connectionMultiplexer.GetDatabase();
            var value = await database.HashGetAsync(CartKey(cartId), VersionField).WaitAsync(ct);
            return value.HasValue ? (long)value : 0L;
        }, cancellationToken).AsTask();
    }

    /// <summary>
    /// Milestone 86: reconciles operations a client tracked while offline
    /// (a different tab, a device that lost connectivity) against whatever
    /// is currently stored, via CartCrdtState.Merge - see that type for the
    /// no-resurrection and add-wins properties this buys over the plain
    /// last-write-wins upserts above. Read-modify-write against a single
    /// Redis key, not compare-and-swap: this store already accepts that
    /// race for every other mutation (see UpsertItemAsync), and the CRDT
    /// merge itself is what makes a lost race here harmless rather than
    /// silently wrong - re-running Merge with the same inputs is idempotent,
    /// so a retried merge after a lost race converges anyway.
    /// </summary>
    public Task<IReadOnlyList<CartLineItem>> MergeAsync(
        string cartId, CartCrdtState clientState, CancellationToken cancellationToken)
    {
        return _pipeline.ExecuteAsync(async ct =>
        {
            var database = connectionMultiplexer.GetDatabase();
            var key = CartKey(cartId);

            var entries = await database.HashGetAllAsync(key).WaitAsync(ct);
            var currentItems = entries
                .Where(entry => entry.Name != VersionField)
                .Select(entry => Deserialize(entry.Value!))
                .ToList();

            var serverState = ToServerCrdtState(currentItems);
            var merged = CartCrdtState.Merge(serverState, clientState);
            var mergedItems = merged.ToLineItems();

            // Whole-hash replace (bar the version field) rather than a
            // per-field diff: simpler, and correct regardless of which
            // SKUs the merge added, changed, or dropped - a diff would
            // just be this same computation done twice.
            var realFields = entries.Where(entry => entry.Name != VersionField).Select(entry => entry.Name).ToArray();
            if (realFields.Length > 0)
            {
                await database.HashDeleteAsync(key, realFields).WaitAsync(ct);
            }

            if (mergedItems.Count > 0)
            {
                var fields = mergedItems
                    .Select(item => new HashEntry(item.Sku, JsonSerializer.Serialize(item, SerializerOptions)))
                    .ToArray();
                await database.HashSetAsync(key, fields).WaitAsync(ct);
            }

            await database.HashIncrementAsync(key, VersionField).WaitAsync(ct);
            await database.KeyExpireAsync(key, TimeSpan.FromSeconds(_options.TimeToLiveSeconds)).WaitAsync(ct);

            return (IReadOnlyList<CartLineItem>)mergedItems.OrderBy(item => item.AddedAt).ToList();
        }, cancellationToken).AsTask();
    }

    /// <summary>
    /// Treats whatever is currently stored as if it came from one
    /// server-side replica - a single synthetic dot per present SKU is
    /// enough for CartItemCrdt.Merge to reason about correctly, since the
    /// server's own state was never itself divergent (this store is the
    /// only writer that isn't a merge).
    /// </summary>
    private static CartCrdtState ToServerCrdtState(IReadOnlyList<CartLineItem> currentItems)
    {
        var items = new Dictionary<string, CartItemCrdt>(StringComparer.Ordinal);
        var metadata = new Dictionary<string, CartItemMetadata>(StringComparer.Ordinal);

        foreach (var item in currentItems)
        {
            var dot = new CartDot("server", item.AddedAt.ToUnixTimeMilliseconds());
            items[item.Sku] = new CartItemCrdt(
                new HashSet<CartDot> { dot },
                new HashSet<CartDot>(),
                new Dictionary<string, (long, long)> { ["server"] = (item.Quantity, 0) });
            metadata[item.Sku] = new CartItemMetadata(item.ProductName, item.UnitPrice, item.Currency, item.AddedAt);
        }

        return new CartCrdtState(items, metadata);
    }

    private const string VersionField = "__version";

    private static RedisKey CartKey(string cartId) => $"cart:{cartId}";

    private static CartLineItem Deserialize(RedisValue value)
    {
        return JsonSerializer.Deserialize<CartLineItem>((string)value!, SerializerOptions)
            ?? throw new InvalidOperationException("A cart line item value deserialized to null.");
    }
}
