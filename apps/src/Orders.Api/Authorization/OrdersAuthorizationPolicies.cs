namespace Orders.Api.Authorization;

public static class OrdersAuthorizationPolicies
{
    public const string Read = "orders:read";
    public const string Write = "orders:write";

    /// <summary>Cross-customer access for support agents or fulfilment tooling; also satisfies the Read/Write roles.</summary>
    public const string Admin = "orders:admin";
}
