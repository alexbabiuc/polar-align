using System.Runtime.CompilerServices;

// AscomMapping is the pure, COM-free seam this project's tests exercise
// directly (see tests/FreePolarAlign.Tests.Devices/AscomMappingTests.cs);
// everything else in this assembly touches a live COM object and cannot be
// genuinely tested on this (macOS) development machine.
[assembly: InternalsVisibleTo("FreePolarAlign.Tests.Devices")]
