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

    /// <summary>Drops the cached order so the next read sees the new status.</summary>
    Task InvalidateAsync(Guid id, CancellationToken cancellationToken);
}
