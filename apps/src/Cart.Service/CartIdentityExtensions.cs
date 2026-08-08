namespace Cart.Service;

/// <summary>
/// Milestone 84: the same preferred_username-first identity read
/// Orders.Api's CallerIdentityExtensions uses (Milestone 83) - this
/// service has no reference to Orders.Api's assembly, so the handful of
/// lines are duplicated here rather than shared, the same tradeoff this
/// codebase already made for the JWT-bearer wiring itself. Keeping the
/// same claim priority matters: an order's customerId and a cart's key
/// must agree on who "customer-42" is, or the cart Storefront reads while
/// building a checkout request would belong to someone else's identity
/// than the order it's about to become.
/// </summary>
public static class CartIdentityExtensions
{
    public static string GetCustomerId(this HttpContext context) =>
        context.User.FindFirst("preferred_username")?.Value
            ?? context.User.FindFirst("sub")?.Value
            ?? throw new InvalidOperationException("The authenticated caller has neither a preferred_username nor a sub claim.");
}
