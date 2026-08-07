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
    /// status.
    ///
    /// Until now every status change happened in Orders.Worker, which has
    /// its own IOrderCacheInvalidator - so this port only ever needed to
    /// read. The fulfilment API changes status too, and without this the
    /// order would keep reporting Confirmed for the whole cache TTL after
    /// it had already shipped and been delivered.
    /// </summary>
    Task InvalidateAsync(Guid id, CancellationToken cancellationToken);
}
