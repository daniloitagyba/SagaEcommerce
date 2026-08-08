namespace Orders.Application;

/// <summary>
/// Who is calling a use case, carried in from the transport
/// layer rather than read from ASP.NET Core types directly - the
/// Application layer does not (and per the architecture fitness functions,
/// must not) depend on HttpContext or JWT claims. Orders.Api's
/// CallerIdentityExtensions is the only place that builds one.
/// </summary>
public readonly record struct CallerIdentity(string? CustomerId, bool IsAdmin)
{
    /// <summary>
    /// Whether this caller may act on data belonging to
    /// <paramref name="resourceCustomerId"/> - either they own it, or they
    /// hold the admin role. A null resource owner is never owned by a
    /// non-admin caller: absence of proof is not proof of ownership.
    /// </summary>
    public bool MayAccess(string? resourceCustomerId) =>
        IsAdmin || (resourceCustomerId is not null && string.Equals(resourceCustomerId, CustomerId, StringComparison.Ordinal));
}
