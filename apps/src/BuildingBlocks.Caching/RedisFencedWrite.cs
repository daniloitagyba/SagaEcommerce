using StackExchange.Redis;

namespace BuildingBlocks;

/// <summary>Fencing-token helpers that reject stale Redis writes from a lock holder that resumes after its lock expired.</summary>
public static class RedisFencedWrite
{
    private const string FencedSetScript = """
        local valueKey = KEYS[1]
        local fenceKey = KEYS[2]
        local token = tonumber(ARGV[1])
        local value = ARGV[2]
        local ttlSeconds = ARGV[3]
        local current = redis.call('GET', fenceKey)
        if current and tonumber(current) > token then
            return 0
        end
        redis.call('SET', valueKey, value, 'EX', ttlSeconds)
        redis.call('SET', fenceKey, token, 'EX', ttlSeconds)
        return 1
        """;

    public static Task<long> NextFenceTokenAsync(this IDatabase database, string fenceSequenceKey) =>
        database.StringIncrementAsync(fenceSequenceKey);

    /// <summary>Writes <paramref name="value"/> to <paramref name="valueKey"/> only if <paramref name="token"/> is not older than the last token recorded there.</summary>
    public static async Task<bool> FencedSetAsync(
        this IDatabase database,
        string valueKey,
        long token,
        string value,
        TimeSpan timeToLive)
    {
        var result = await database.ScriptEvaluateAsync(
            FencedSetScript,
            [(RedisKey)valueKey, (RedisKey)FenceKey(valueKey)],
            [token, value, (long)timeToLive.TotalSeconds]);

        return (long)result == 1;
    }

    private static string FenceKey(string valueKey) => $"{valueKey}:fence";
}
