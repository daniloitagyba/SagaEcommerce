namespace Orders.Api.Authorization;

public static class OrdersAuthorizationPolicies
{
    public const string Read = "orders:read";
    public const string Write = "orders:write";

    /// <summary>
    /// Milestone 83: cross-customer access - a support agent or the
    /// warehouse's own fulfilment tooling, not a shopper. Read/Write
    /// (below) both also accept this role, so an admin token needs only
    /// one role to do everything a plain shopper token can, plus what
    /// admin-only routes require.
    /// </summary>
    public const string Admin = "orders:admin";
}
