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
            if (removed && await database.HashLengthAsync(key).WaitAsync(ct) > 0)
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

    private static RedisKey CartKey(string cartId) => $"cart:{cartId}";

    private static CartLineItem Deserialize(RedisValue value)
    {
        return JsonSerializer.Deserialize<CartLineItem>((string)value!, SerializerOptions)
            ?? throw new InvalidOperationException("A cart line item value deserialized to null.");
    }
}
