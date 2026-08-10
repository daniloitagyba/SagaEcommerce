using System.Runtime.CompilerServices;

// Lets Catalog.UnitTests exercise the request-normalization/validation
// helpers directly (they stay internal rather than public - wired into the
// route table, not a public API surface other services call into).
[assembly: InternalsVisibleTo("Catalog.UnitTests")]
