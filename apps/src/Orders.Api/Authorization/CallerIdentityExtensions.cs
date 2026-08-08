using Orders.Application;

namespace Orders.Api.Authorization;

/// <summary>
/// Who is actually calling, verified rather than
/// self-asserted - the piece deliberately left for later when JWT auth was
/// first wired in (that earlier work built application identity, "which
/// client is calling"; this is end-user identity, "which shopper is calling").
///
/// Reads <c>preferred_username</c> rather than the OIDC-standard
/// <c>sub</c> claim: this lab's <c>customerId</c> has been a human-readable
/// string ("customer-42") from the start, threaded through coupons,
/// loyalty tiers, and order history. Provisioning Keycloak users under
/// those same usernames (<c>scripts/keycloak-configure-realm.sh</c>) lets
/// every existing consumer of <c>customerId</c> keep working unchanged - no
/// parallel identity migration. <c>sub</c> is a fallback for a token that
/// carries no username, not the primary source; a system that needed
/// collision-proof identity independent of a mutable username would make
/// the opposite choice, and this lab's `customerId` scheme is the reason it
/// doesn't have to here.
/// </summary>
public static class CallerIdentityExtensions
{
    /// <summary>
    /// The authenticated caller's own customer id, or null if the token
    /// carries neither claim (a client-credentials token with no human
    /// behind it - only ever expected to reach an Admin-gated route).
    /// </summary>
    public static string? GetCustomerId(this HttpContext context) =>
        context.User.FindFirst("preferred_username")?.Value
            ?? context.User.FindFirst("sub")?.Value;

    /// <summary>Cross-customer access - see OrdersAuthorizationPolicies.Admin.</summary>
    public static bool IsAdmin(this HttpContext context) =>
        context.User.IsInRole(OrdersAuthorizationPolicies.Admin);

    /// <summary>The Application-layer value built from this request's claims - see CallerIdentity for why it isn't just passed as HttpContext.</summary>
    public static CallerIdentity GetCallerIdentity(this HttpContext context) =>
        new(context.GetCustomerId(), context.IsAdmin());

    /// <summary>
    /// Whether the caller may act on data belonging to
    /// <paramref name="resourceCustomerId"/>. A read-only-endpoint
    /// shorthand for <c>GetCallerIdentity(context).MayAccess(...)</c> - a
    /// write path that mutates anything should call
    /// <see cref="GetCallerIdentity"/> once and pass it into the handler,
    /// so the check happens before the mutation, not on the response after.
    /// </summary>
    public static bool MayAccess(this HttpContext context, string? resourceCustomerId) =>
        context.GetCallerIdentity().MayAccess(resourceCustomerId);
}
