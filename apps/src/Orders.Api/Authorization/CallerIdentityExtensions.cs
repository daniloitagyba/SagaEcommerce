using Orders.Application;

namespace Orders.Api.Authorization;

/// <summary>Resolves the authenticated caller's identity from JWT claims.</summary>
public static class CallerIdentityExtensions
{
    /// <summary>Gets the authenticated caller's own customer id, or null if the token carries neither claim.</summary>
    public static string? GetCustomerId(this HttpContext context) =>
        context.User.FindFirst("preferred_username")?.Value
            ?? context.User.FindFirst("sub")?.Value;

    /// <summary>Determines whether the caller has cross-customer admin access.</summary>
    public static bool IsAdmin(this HttpContext context) =>
        context.User.IsInRole(OrdersAuthorizationPolicies.Admin);

    /// <summary>Builds the Application-layer caller identity from this request's claims.</summary>
    public static CallerIdentity GetCallerIdentity(this HttpContext context) =>
        new(context.GetCustomerId(), context.IsAdmin());

    /// <summary>Determines whether the caller may act on data belonging to the given customer.</summary>
    /// <param name="resourceCustomerId">The customer id owning the resource being accessed.</param>
    public static bool MayAccess(this HttpContext context, string? resourceCustomerId) =>
        context.GetCallerIdentity().MayAccess(resourceCustomerId);
}
