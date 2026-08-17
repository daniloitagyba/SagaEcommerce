namespace Cart.Service;

/// <summary>Resolves the caller's customer id from the preferred_username claim, falling back to sub.</summary>
public static class CartIdentityExtensions
{
    public static string GetCustomerId(this HttpContext context) =>
        context.User.FindFirst("preferred_username")?.Value
            ?? context.User.FindFirst("sub")?.Value
            ?? throw new InvalidOperationException("The authenticated caller has neither a preferred_username nor a sub claim.");
}
