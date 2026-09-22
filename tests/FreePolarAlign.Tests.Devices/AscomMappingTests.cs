using FreePolarAlign.Devices;
using FreePolarAlign.Devices.Ascom;
using Xunit;

namespace FreePolarAlign.Tests.Devices;

/// <summary>
/// Covers <see cref="AscomMapping"/>, the one piece of
/// FreePolarAlign.Devices.Ascom that involves no COM object and can
/// therefore be genuinely exercised on this (macOS) development machine.
/// Everything else in that project (AscomMount, AscomCamera,
/// AscomDeviceProvider) talks to a live ASCOM driver via late-bound COM and
/// is compiled-but-unverified -- see that project's doc comments and
/// docs/MOUNT-COMPATIBILITY.md.
/// </summary>
public sealed class AscomMappingTests
{
    [Theory]
    [InlineData(0, PierSide.East)]
    [InlineData(1, PierSide.West)]
    [InlineData(-1, PierSide.Unknown)]
    [InlineData(42, PierSide.Unknown)]
    [InlineData(int.MinValue, PierSide.Unknown)]
    public void MapPierSide_MapsKnownAscomValues_AndDefaultsUnknownOtherwise(int ascomValue, PierSide expected)
    {
        Assert.Equal(expected, AscomMapping.MapPierSide(ascomValue));
    }

    /// <summary>
    /// Written as a single fact rather than a theory because
    /// <see cref="AscomEquatorialSystem"/> is internal to the Ascom assembly, so
    /// it cannot appear in a public test method's signature.
    /// </summary>
    [Fact]
    public void MapEquatorialSystem_MapsKnownAscomValues_AndDefaultsOtherwise()
    {
        Assert.Equal(AscomEquatorialSystem.Other, AscomMapping.MapEquatorialSystem(0));
        Assert.Equal(AscomEquatorialSystem.Topocentric, AscomMapping.MapEquatorialSystem(1));
        Assert.Equal(AscomEquatorialSystem.J2000, AscomMapping.MapEquatorialSystem(2));
        Assert.Equal(AscomEquatorialSystem.J2050, AscomMapping.MapEquatorialSystem(3));
        Assert.Equal(AscomEquatorialSystem.B1950, AscomMapping.MapEquatorialSystem(4));

        // A driver bug or a future enum member is Other, never a guess at the
        // nearest known system.
        Assert.Equal(AscomEquatorialSystem.Other, AscomMapping.MapEquatorialSystem(-1));
        Assert.Equal(AscomEquatorialSystem.Other, AscomMapping.MapEquatorialSystem(99));
        Assert.Equal(AscomEquatorialSystem.Other, AscomMapping.MapEquatorialSystem(int.MinValue));
    }

    [Theory]
    [InlineData(0.0, 0.0)]
    [InlineData(1.0, 15.0)]
    [InlineData(24.0, 360.0)]
    [InlineData(12.5, 187.5)]
    public void RaHoursToDegrees_MultipliesByFifteen(double hours, double expectedDegrees)
    {
        Assert.Equal(expectedDegrees, AscomMapping.RaHoursToDegrees(hours), precision: 10);
    }

    [Theory]
    [InlineData(0.0, 0.0)]
    [InlineData(15.0, 1.0)]
    [InlineData(360.0, 24.0)]
    [InlineData(187.5, 12.5)]
    public void RaDegreesToHours_IsExactInverseOfHoursToDegrees(double degrees, double expectedHours)
    {
        Assert.Equal(expectedHours, AscomMapping.RaDegreesToHours(degrees), precision: 10);
    }

    [Fact]
    public void RaConversion_RoundTrips()
    {
        const double originalDegrees = 271.2345;
        double hours = AscomMapping.RaDegreesToHours(originalDegrees);
        double roundTripped = AscomMapping.RaHoursToDegrees(hours);
        Assert.Equal(originalDegrees, roundTripped, precision: 9);
    }

    [Fact]
    public void ComputeExposureMidpointUtc_IsHalfwayThroughCommandedDuration()
    {
        var start = new DateTime(2026, 9, 9, 3, 0, 0, DateTimeKind.Utc);
        TimeSpan duration = TimeSpan.FromSeconds(10);

        DateTime midpoint = AscomMapping.ComputeExposureMidpointUtc(start, duration);

        Assert.Equal(start + TimeSpan.FromSeconds(5), midpoint);
    }

    [Fact]
    public void ComputeExposureMidpointUtc_IgnoresWallClockOverrun_UsesCommandedDurationOnly()
    {
        // Regression guard for the documented rationale: a slow readout after
        // the shutter closes must not shift the midpoint later, since it is
        // computed purely from the commanded duration, not observed elapsed time.
        var start = new DateTime(2026, 9, 9, 3, 0, 0, DateTimeKind.Utc);
        TimeSpan commandedDuration = TimeSpan.FromSeconds(4);

        DateTime midpoint = AscomMapping.ComputeExposureMidpointUtc(start, commandedDuration);

        Assert.Equal(start.AddSeconds(2), midpoint);
    }

    [Fact]
    public void ComputeExposureMidpointUtc_ZeroDuration_IsStartTime()
    {
        var start = new DateTime(2026, 9, 9, 3, 0, 0, DateTimeKind.Utc);

        Assert.Equal(start, AscomMapping.ComputeExposureMidpointUtc(start, TimeSpan.Zero));
    }
}
