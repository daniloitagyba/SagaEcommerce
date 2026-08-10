using System.Runtime.CompilerServices;

// Lets Cart.UnitTests exercise the offline-merge reconciliation logic
// directly (it stays internal rather than public - wired into the route
// table, not a public API surface other services call into).
[assembly: InternalsVisibleTo("Cart.UnitTests")]
