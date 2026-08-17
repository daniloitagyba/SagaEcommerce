namespace Orders.Application;

/// <summary>
/// Identifies a use-case caller.
/// </summary>
public readonly record struct CallerIdentity(string? CustomerId, bool IsAdmin)
{
    /// <summary>
    /// Whether this caller may act on data belonging to <paramref name="resourceCustomerId"/>.
    /// </summary>
    public bool MayAccess(string? resourceCustomerId) =>
        IsAdmin || (resourceCustomerId is not null && string.Equals(resourceCustomerId, CustomerId, StringComparison.Ordinal));
}
