using System.Runtime.CompilerServices;

// Lets Payments.UnitTests exercise deserialize/validation helpers directly
// (they stay internal rather than public - only ever called from within
// this service's own message processors).
[assembly: InternalsVisibleTo("Payments.UnitTests")]
