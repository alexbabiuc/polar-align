using System.Runtime.CompilerServices;

// Exposes the internal request-to-backend translation helpers (field-radius
// conversion, search-strategy selection, cancellation classification, failure
// mapping) to the test project so they can be unit-tested directly without
// running a real solve (see tests/FreePolarAlign.Tests.Solving).
[assembly: InternalsVisibleTo("FreePolarAlign.Tests.Solving")]
