using FreePolarAlign.Core.Alignment;
using FreePolarAlign.Core.Astrometry;
using Xunit;

namespace FreePolarAlign.Tests.Core;

/// <summary>
/// The mechanical model that makes the virtual observatory a real test: the same
/// two angles read off an ideally aligned mount, then driven on a misaligned
/// one. If these invariants do not hold, every simulated pointing is wrong in a
/// way that would be very hard to notice downstream.
/// </summary>
public class MountMechanicsTests
{
    /// <summary>
    /// With no misalignment and no cone error the pair must be exact inverses.
    /// This is what guarantees a perfectly aligned simulated mount points where
    /// it is told, so that any error seen later is the injected one and not an
    /// artefact of the frame conventions.
    /// </summary>
    [Theory]
    [InlineData(45.0)]
    [InlineData(60.0)]
    [InlineData(0.1)]
    [InlineData(-33.9)]
    [InlineData(-55.0)]
    public void ComposeInvertsDecompose_WhenPerfectlyAligned(double latitude)
    {
        foreach (double altitude in new[] { 15.0, 35.0, 55.0, 75.0 })
        {
            foreach (double azimuth in new[] { 0.0, 47.0, 123.0, 200.0, 315.0 })
            {
                var direction = new HorizontalCoordinates(azimuth, altitude);

                var (rotation, declination) = MountMechanics.Decompose(latitude, direction);
                HorizontalCoordinates recomposed = MountMechanics.Compose(
                    latitude, MountMisalignment.Aligned, rotation, declination);

                double errorArcseconds = SeparationArcseconds(direction, recomposed);
                Assert.True(errorArcseconds < 1e-6,
                    $"latitude {latitude}, az {azimuth}, alt {altitude}: round trip off by {errorArcseconds:E3}\"");
            }
        }
    }

    /// <summary>
    /// A misaligned mount driven to the angles an aligned one would use must
    /// miss by an amount that grows with the misalignment. Checked as an
    /// inequality and a scale rather than an exact figure, because the exact
    /// miss depends on where in the sky it is pointing -- which is itself the
    /// reason a single pointing cannot reveal the misalignment and a swept
    /// circle can.
    /// </summary>
    [Fact]
    public void MisalignmentDisplacesThePointing_InProportionToItsSize()
    {
        const double latitude = 45.0;
        var direction = new HorizontalCoordinates(120.0, 50.0);
        var (rotation, declination) = MountMechanics.Decompose(latitude, direction);

        double previous = 0.0;
        foreach (double magnitude in new[] { 1.0, 10.0, 60.0, 120.0 })
        {
            var misalignment = new MountMisalignment(magnitude * 0.6, magnitude * 0.8);
            HorizontalCoordinates achieved = MountMechanics.Compose(latitude, misalignment, rotation, declination);

            double missArcminutes = SeparationArcseconds(direction, achieved) / 60.0;

            Assert.True(missArcminutes > previous,
                $"a {magnitude}' misalignment should miss by more than a smaller one did");

            // The pointing error is of the same order as the axis error, never
            // wildly larger: a mount 1' out does not miss by a degree.
            Assert.True(missArcminutes < 3.0 * magnitude,
                $"a {magnitude}' misalignment displaced the pointing by {missArcminutes:F2}', which is implausibly large");

            previous = missArcminutes;
        }
    }

    /// <summary>
    /// Cone error must move the pointing by exactly its own angle, and must not
    /// depend on the mount's rotation: it is a fixed offset in the rotating
    /// frame, which is the property the whole method relies on.
    /// </summary>
    [Fact]
    public void ConeErrorOffsetsByItsOwnAngle_AtEveryRotation()
    {
        const double latitude = 45.0;
        const double coneArcminutes = 30.0;

        foreach (double rotation in new[] { -60.0, -20.0, 0.0, 25.0, 70.0 })
        {
            HorizontalCoordinates without = MountMechanics.Compose(
                latitude, MountMisalignment.Aligned, rotation, declinationDegrees: 40.0);
            HorizontalCoordinates with = MountMechanics.Compose(
                latitude, MountMisalignment.Aligned, rotation, declinationDegrees: 40.0,
                coneErrorArcminutes: coneArcminutes, conePhaseDegrees: 37.0);

            double offsetArcminutes = SeparationArcseconds(without, with) / 60.0;
            Assert.Equal(coneArcminutes, offsetArcminutes, precision: 4);
        }
    }

    /// <summary>
    /// Sweeping the rotation traces a circle about the polar axis whose radius
    /// is the complement of the declination. This is the geometric claim the
    /// small-circle fit is built on, verified directly on the mechanics rather
    /// than through the fit.
    /// </summary>
    [Fact]
    public void SweepingRotationTracesACircleAboutTheAxis()
    {
        const double latitude = 45.0;
        const double declination = 40.0;
        var misalignment = new MountMisalignment(12.0, -9.0);

        HorizontalCoordinates axis = MountForwardModel.MountAxis(latitude, misalignment);

        foreach (double rotation in new[] { -75.0, -40.0, -10.0, 15.0, 50.0, 85.0 })
        {
            HorizontalCoordinates pointing = MountMechanics.Compose(latitude, misalignment, rotation, declination);
            double radiusDegrees = SeparationArcseconds(axis, pointing) / 3600.0;

            Assert.Equal(90.0 - declination, radiusDegrees, precision: 6);
        }
    }

    /// <summary>
    /// The property everything depends on, and the one an earlier version got
    /// wrong: sweeping the rotation *with cone error present* must still trace a
    /// circle about the polar axis.
    ///
    /// Cone error is a fixed offset within the rotating mount frame, so it must
    /// change the circle's radius and nothing else. Applying it in a basis
    /// derived from the current pointing instead leaves the radius varying as
    /// the mount turns -- which is not a circle, makes the fit invalid, and is
    /// invisible unless the radius is checked at several rotations rather than
    /// just one.
    /// </summary>
    [Theory]
    [InlineData(0.0)]
    [InlineData(20.0)]
    [InlineData(90.0)]
    [InlineData(300.0)]
    public void SweepingRotationTracesACircle_EvenWithConeError(double coneErrorArcminutes)
    {
        const double latitude = 45.0;
        const double declination = 70.0;
        var misalignment = new MountMisalignment(84.85, 120.0);
        HorizontalCoordinates axis = MountForwardModel.MountAxis(latitude, misalignment);

        foreach (double phase in new[] { 0.0, 35.0, 140.0, 265.0 })
        {
            var radii = new List<double>();
            foreach (double rotation in new[] { 5.0, 19.0, 33.0, 47.0, 61.0, 75.0 })
            {
                HorizontalCoordinates pointing = MountMechanics.Compose(
                    latitude, misalignment, rotation, declination, coneErrorArcminutes, phase);
                radii.Add(SeparationArcseconds(axis, pointing));
            }

            double spread = radii.Max() - radii.Min();
            Assert.True(spread < 0.01,
                $"cone {coneErrorArcminutes}' phase {phase}°: radius varied by {spread:F3}\" across the sweep, " +
                "so the track is not a circle");
        }
    }

    private static double SeparationArcseconds(HorizontalCoordinates a, HorizontalCoordinates b)
    {
        double d2r = Math.PI / 180.0;
        double lat1 = a.AltitudeDegrees * d2r, lat2 = b.AltitudeDegrees * d2r;
        double dLat = lat2 - lat1;
        double dLon = (b.AzimuthDegrees - a.AzimuthDegrees) * d2r;

        double h = Math.Sin(dLat / 2.0) * Math.Sin(dLat / 2.0)
                 + Math.Cos(lat1) * Math.Cos(lat2) * Math.Sin(dLon / 2.0) * Math.Sin(dLon / 2.0);
        return 2.0 * Math.Asin(Math.Sqrt(Math.Clamp(h, 0.0, 1.0))) * 180.0 / Math.PI * 3600.0;
    }
}
