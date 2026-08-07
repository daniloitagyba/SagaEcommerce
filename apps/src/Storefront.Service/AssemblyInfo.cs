using System.Runtime.CompilerServices;

// Milestone 66: lets Storefront.UnitTests exercise CheckoutAsync directly
// (it stays internal rather than public - it's wired into the route table,
// not a public API surface other services are meant to call into).
[assembly: InternalsVisibleTo("Storefront.UnitTests")]
