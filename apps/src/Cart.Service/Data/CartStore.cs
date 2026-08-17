using System.Text.Json;
using Cart.Service.Domain;
using Microsoft.Extensions.Options;
using Polly;
using Polly.Registry;
using StackExchange.Redis;

namespace Cart.Service.Data;

public sealed record CartSnapshot(
    string CartId,
    IReadOnlyList<CartLineItem> Items,
    TimeSpan? TimeToLive,
    long Version,
    CartCrdtState State);

/// <summary>Redis-backed cart store; Redis is the system of record, not a cache, and a cart is a single Redis Hash.</summary>
public sealed class CartStore(
    IConnectionMultiplexer connectionMultiplexer,
    ResiliencePipelineProvider<string> pipelineProvider,
    IOptions<CartOptions> options)
{
    public const string ResiliencePipelineName = "cart-redis";
    private const int MaximumCasAttempts = 100;

    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);
    private readonly CartOptions _options = options.Value;
    private readonly ResiliencePipeline _pipeline = pipelineProvider.GetPipeline(ResiliencePipelineName);

    public Task<CartSnapshot> GetSnapshotAsync(string ownerId, CancellationToken cancellationToken)
    {
        return _pipeline.ExecuteAsync(async ct =>
        {
            var database = connectionMultiplexer.GetDatabase();
            var result = await database.ScriptEvaluateAsync(
                ReadSnapshotScript,
                [CartKey(ownerId)],
                []).WaitAsync(ct);
            var values = (RedisResult[]?)result;
            if (values is null || values.Length == 0)
            {
                return new CartSnapshot(string.Empty, [], null, 0, CartCrdtState.Empty);
            }

            var ttlMilliseconds = long.Parse(values[0].ToString(), System.Globalization.CultureInfo.InvariantCulture);
            var cartId = string.Empty;
            var version = 0L;
            var items = new List<CartLineItem>();
            var entries = new List<HashEntry>();

            for (var index = 1; index + 1 < values.Length; index += 2)
            {
                var field = values[index].ToString();
                var value = values[index + 1].ToString();
                entries.Add(new HashEntry(field, value));
                if (field == VersionField)
                {
                    version = long.Parse(value, System.Globalization.CultureInfo.InvariantCulture);
                }
                else if (field == CartIdField)
                {
                    cartId = value;
                }
                else if (!IsMetadataField(field))
                {
                    items.Add(Deserialize(value));
                }
            }

            return new CartSnapshot(
                cartId,
                items
                .OrderBy(item => item.AddedAt)
                .ToList(),
                ttlMilliseconds >= 0 ? TimeSpan.FromMilliseconds(ttlMilliseconds) : null,
                version,
                CartCrdtCodec.Read(entries, IsMetadataField));
        }, cancellationToken).AsTask();
    }

    public async Task<IReadOnlyList<CartLineItem>> GetAsync(string ownerId, CancellationToken cancellationToken) =>
        (await GetSnapshotAsync(ownerId, cancellationToken)).Items;

    public Task<CartLineItem?> GetItemAsync(string cartId, string sku, CancellationToken cancellationToken)
    {
        return _pipeline.ExecuteAsync(async ct =>
        {
            var database = connectionMultiplexer.GetDatabase();
            var value = await database.HashGetAsync(CartKey(cartId), sku).WaitAsync(ct);
            return value.HasValue ? Deserialize(value!) : null;
        }, cancellationToken).AsTask();
    }

    public Task UpsertItemAsync(string ownerId, CartLineItem item, CancellationToken cancellationToken)
    {
        return MutateAsync(
            ownerId,
            state =>
            {
                var current = state.Items.GetValueOrDefault(item.Sku, CartItemCrdt.Empty);
                var currentQuantity = current.EffectiveQuantity;
                var metadata = new CartItemMetadata(
                    item.ProductName, item.UnitPrice, item.Currency, item.AddedAt);
                var next = item.Quantity > currentQuantity
                    ? state.Increase(
                        item.Sku,
                        "server",
                        item.Quantity - currentQuantity,
                        NextServerDot(state),
                        metadata)
                    : item.Quantity < currentQuantity
                        ? state.Decrease(item.Sku, "server", currentQuantity - item.Quantity)
                        : state;

                if (!current.IsPresent)
                {
                    next = next.Increase(
                        item.Sku, "server", 0, NextServerDot(next), metadata);
                }

                return (next, true);
            },
            cancellationToken);
    }

    /// <summary>Refreshes a cart line's snapshotted price/name without touching its CRDT quantity state.</summary>
    public Task<bool> RefreshItemPriceAsync(string ownerId, string sku, CartItemMetadata metadata, CancellationToken cancellationToken)
    {
        return MutateAsync(
            ownerId,
            state =>
            {
                var refreshed = state.RefreshMetadata(sku, metadata);
                return (refreshed, !ReferenceEquals(refreshed, state));
            },
            cancellationToken);
    }

    public Task<bool> RemoveItemAsync(string ownerId, string sku, CancellationToken cancellationToken)
    {
        return MutateAsync(
            ownerId,
            state => state.Items.TryGetValue(sku, out var item) && item.IsPresent
                ? (state.Remove(sku), true)
                : (state, false),
            cancellationToken);
    }

    public Task<bool> ClearAsync(string cartId, CancellationToken cancellationToken)
    {
        return _pipeline.ExecuteAsync(async ct =>
        {
            var database = connectionMultiplexer.GetDatabase();
            return await database.KeyDeleteAsync(CartKey(cartId)).WaitAsync(ct);
        }, cancellationToken).AsTask();
    }

    public Task<bool> ClearIfVersionAsync(
        string ownerId,
        string cartId,
        long expectedVersion,
        CancellationToken cancellationToken)
    {
        return _pipeline.ExecuteAsync(async ct =>
        {
            var database = connectionMultiplexer.GetDatabase();
            var cleared = await database.ScriptEvaluateAsync(
                ClearIfVersionScript,
                [CartKey(ownerId)],
                [CartIdField, cartId, VersionField, expectedVersion]).WaitAsync(ct);
            return (long)cleared == 1;
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

    /// <summary>Returns the cart's monotonically increasing version, bumped on every mutation.</summary>
    public Task<long> GetVersionAsync(string cartId, CancellationToken cancellationToken)
    {
        return _pipeline.ExecuteAsync(async ct =>
        {
            var database = connectionMultiplexer.GetDatabase();
            var value = await database.HashGetAsync(CartKey(cartId), VersionField).WaitAsync(ct);
            return value.HasValue ? (long)value : 0L;
        }, cancellationToken).AsTask();
    }

    /// <summary>Reconciles client-tracked offline operations against the current stored state via a versioned compare-and-swap.</summary>
    public Task<IReadOnlyList<CartLineItem>> MergeAsync(
        string cartId, CartCrdtState clientState, CancellationToken cancellationToken)
    {
        return _pipeline.ExecuteAsync(async ct =>
        {
            var database = connectionMultiplexer.GetDatabase();
            var key = CartKey(cartId);

            for (var attempt = 0; attempt < MaximumCasAttempts; attempt++)
            {
                var entries = await database.HashGetAllAsync(key).WaitAsync(ct);
                var expectedVersionValue = entries.FirstOrDefault(entry => entry.Name == VersionField).Value;
                var expectedVersion = expectedVersionValue.HasValue ? (long)expectedVersionValue : 0L;
                var serverState = CartCrdtCodec.Read(entries, IsMetadataField);
                var merged = CartCrdtState.Merge(serverState, clientState);
                var replaced = await TryWriteStateAsync(database, key, merged, expectedVersion, ct);

                if (replaced)
                {
                    return (IReadOnlyList<CartLineItem>)merged.ToLineItems().OrderBy(item => item.AddedAt).ToList();
                }

                await Task.Delay(TimeSpan.FromMilliseconds(1), ct);
            }

            throw new InvalidOperationException($"The cart changed too frequently to merge after {MaximumCasAttempts} attempts.");
        }, cancellationToken).AsTask();
    }

    private Task<bool> MutateAsync(
        string ownerId,
        Func<CartCrdtState, (CartCrdtState State, bool Changed)> mutation,
        CancellationToken cancellationToken)
    {
        return _pipeline.ExecuteAsync(async ct =>
        {
            var database = connectionMultiplexer.GetDatabase();
            var key = CartKey(ownerId);

            for (var attempt = 0; attempt < MaximumCasAttempts; attempt++)
            {
                var entries = await database.HashGetAllAsync(key).WaitAsync(ct);
                var expectedVersionValue = entries.FirstOrDefault(entry => entry.Name == VersionField).Value;
                var expectedVersion = expectedVersionValue.HasValue ? (long)expectedVersionValue : 0L;
                var (state, changed) = mutation(CartCrdtCodec.Read(entries, IsMetadataField));
                if (!changed)
                {
                    return false;
                }

                if (await TryWriteStateAsync(database, key, state, expectedVersion, ct))
                {
                    return true;
                }

                await Task.Delay(TimeSpan.FromMilliseconds(1), ct);
            }

            throw new InvalidOperationException($"The cart changed too frequently to mutate after {MaximumCasAttempts} attempts.");
        }, cancellationToken).AsTask();
    }

    private async Task<bool> TryWriteStateAsync(
        IDatabase database,
        RedisKey key,
        CartCrdtState state,
        long expectedVersion,
        CancellationToken cancellationToken)
    {
        var newFieldsBySku = state.ToLineItems().ToDictionary(
            item => item.Sku,
            item => JsonSerializer.Serialize(item, SerializerOptions),
            StringComparer.Ordinal);
        var replaced = await database.ScriptEvaluateAsync(
            ReplaceHashScript,
            [key],
            [VersionField, CartIdField, Guid.NewGuid().ToString("N"), expectedVersion,
                (long)_options.TimeToLiveSeconds,
                JsonSerializer.Serialize(newFieldsBySku, SerializerOptions),
                CrdtField,
                CartCrdtCodec.Serialize(state)]).WaitAsync(cancellationToken);
        return (long)replaced == 1;
    }

    private static long NextServerDot(CartCrdtState state) =>
        state.Items.Values
            .SelectMany(item => item.LiveDots.Concat(item.TombstoneDots))
            .Where(dot => string.Equals(dot.ReplicaId, "server", StringComparison.Ordinal))
            .Select(dot => dot.Counter)
            .DefaultIfEmpty(0)
            .Max() + 1;

    private const string VersionField = "__version";
    private const string CartIdField = "__cart_id";
    private const string CrdtField = "__crdt";

    private static bool IsMetadataField(string field) => field.StartsWith("__", StringComparison.Ordinal);

    private const string ReadSnapshotScript = """
        local result = { redis.call('PTTL', KEYS[1]) }
        local entries = redis.call('HGETALL', KEYS[1])
        for _, value in ipairs(entries) do
            table.insert(result, value)
        end
        return result
        """;

    private const string ClearIfVersionScript = """
        local key = KEYS[1]
        if redis.call('HGET', key, ARGV[1]) ~= ARGV[2] then
            return 0
        end
        if tonumber(redis.call('HGET', key, ARGV[3]) or '-1') ~= tonumber(ARGV[4]) then
            return 0
        end
        return redis.call('DEL', key)
        """;

    private const string ReplaceHashScript = """
        local key = KEYS[1]
        local versionField = ARGV[1]
        local cartIdField = ARGV[2]
        local newCartId = ARGV[3]
        local expectedVersion = tonumber(ARGV[4])
        local ttlSeconds = ARGV[5]
        local newFields = cjson.decode(ARGV[6])
        local crdtField = ARGV[7]
        local crdtState = ARGV[8]

        if tonumber(redis.call('HGET', key, versionField) or '0') ~= expectedVersion then
            return 0
        end

        if redis.call('HEXISTS', key, cartIdField) == 0 then
            redis.call('HSET', key, cartIdField, newCartId)
        end

        local existing = redis.call('HKEYS', key)
        for _, field in ipairs(existing) do
            if field ~= versionField and field ~= cartIdField then
                redis.call('HDEL', key, field)
            end
        end

        for sku, payload in pairs(newFields) do
            redis.call('HSET', key, sku, payload)
        end

        redis.call('HSET', key, crdtField, crdtState)

        redis.call('HINCRBY', key, versionField, 1)
        redis.call('EXPIRE', key, ttlSeconds)
        return 1
        """;

    private static RedisKey CartKey(string cartId) => $"cart:{cartId}";

    private static CartLineItem Deserialize(RedisValue value)
    {
        return JsonSerializer.Deserialize<CartLineItem>((string)value!, SerializerOptions)
            ?? throw new InvalidOperationException("A cart line item value deserialized to null.");
    }
}
