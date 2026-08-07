namespace Orders.Application.Ports;

public enum CacheLookupResult
{
    Hit,
    Miss,
    Bypassed
}

public sealed record CacheLookup(CachedOrder? Order, CacheLookupResult Result);

public interface IOrderCache
{
    Task<CacheLookup> GetOrCreateAsync(
        Guid id,
        Func<CancellationToken, Task<CachedOrder?>> factory,
        CancellationToken cancellationToken);

    /// <summary>
    /// Milestone 69: drops the cached order so the next read sees the new
    /// status - needed once the fulfilment API started changing status too,
    /// not just Orders.Worker via its own IOrderCacheInvalidator.
    /// </summary>
    Task InvalidateAsync(Guid id, CancellationToken cancellationToken);
}
