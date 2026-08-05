using StackExchange.Redis;

namespace BuildingBlocks;

/// <summary>
/// Milestone 48: RedisOrderCache and RedisIdempotencyStore both take a
/// Redis lock (LockTakeAsync/LockReleaseAsync) with a timeout before doing
/// slow work (a Postgres read, order creation) and writing the result -
/// the classic Redlock/Kleppmann hazard, since the lock has no fencing.
/// If the holder is paused (GC, CPU contention, a slow dependency) longer
/// than the lock's timeout, a second caller can acquire the same lock and
/// write its own result; when the first, stale holder resumes, it writes
/// too, with no idea it lost the lock, silently clobbering the second
/// holder's newer value with stale data.
///
/// A fencing token closes this without needing the lock itself to know
/// anything changed: every lock acquisition draws a strictly increasing
/// ticket (<see cref="NextFenceTokenAsync"/>, a plain Redis INCR), and the
/// actual write (<see cref="FencedSetAsync"/>) is a Lua script that
/// refuses to apply if a write carrying a higher ticket has already been
/// recorded - a stale holder's write is rejected at the point of write,
/// which is the only place that can enforce it, not at the point of lock
/// acquisition or release.
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
