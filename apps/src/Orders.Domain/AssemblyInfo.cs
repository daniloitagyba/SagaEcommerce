using System.Runtime.CompilerServices;

// Lets Orders.UnitTests construct OrderEvent/OrderSummary directly for
// GetOrderHistoryHandler/ListOrderSummariesHandler tests - both types are
// otherwise only ever materialized by EF Core (private constructor, private
// setters, matching that they're read-model rows nothing else should build
// by hand in production code).
[assembly: InternalsVisibleTo("Orders.UnitTests")]
