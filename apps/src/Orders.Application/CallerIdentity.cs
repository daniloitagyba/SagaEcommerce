namespace Orders.Application;

public readonly record struct CallerIdentity(string? CustomerId, bool IsAdmin)
{
    public bool MayAccess(string? resourceCustomerId) =>
        IsAdmin || (resourceCustomerId is not null && string.Equals(resourceCustomerId, CustomerId, StringComparison.Ordinal));
}
