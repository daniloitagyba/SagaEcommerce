using StackExchange.Redis;

namespace BuildingBlocks;

/// <summary>
/// Milestone 48: RedisOrderCache and RedisIdempotencyStore both take a
/// timed Redis lock before slow work and writing the result - the classic
/// Redlock/Kleppmann hazard, since a holder paused past the lock's timeout
/// can resume and clobber a second holder's newer write with stale data.
/// A fencing token closes this: every lock acquisition draws a strictly
/// increasing ticket (<see cref="NextFenceTokenAsync"/>, a Redis INCR), and
/// the write (<see cref="FencedSetAsync"/>) is a Lua script that rejects a
/// write carrying a lower ticket than one already recorded - enforced at
/// the point of write, the only place that can enforce it.
/// </summary>
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

    /// <summary>
    /// Writes <paramref name="value"/> to <paramref name="valueKey"/> only if
    /// <paramref name="token"/> is not older than the token of whatever was
    /// last written there. Returns false when the write was rejected as
    /// stale - the caller lost a race against a newer holder and should not
    /// treat its own write as having taken effect.
    /// </summary>
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
