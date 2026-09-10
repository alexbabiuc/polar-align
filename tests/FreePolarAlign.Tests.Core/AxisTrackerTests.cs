using FreePolarAlign.Core.Alignment;
using FreePolarAlign.Core.Astrometry;
using Xunit;

namespace FreePolarAlign.Tests.Core;

/// <summary>
/// Freeze-and-track, which the roadmap calls the feature people will judge the
/// software by: keeping the polar axis estimate live from a single field while
/// the user turns the bolts.
///
/// The truth here is generated with <see cref="MountMechanics"/>, so a bolt turn
/// is expressed the same way the simulator and the fit express it -- a change in
/// the injected misalignment -- rather than by reusing the tracker's own idea of
/// what a bolt does, which would make these tests circular.
/// </summary>
public class AxisTrackerTests
{
    private const double Latitude = 45.0;
    private const double MechanicalDeclination = 40.0;
    /// <summary>
    /// Chosen for usable tracking geometry, and not arbitrarily. At this
    /// latitude and declination a rotation of 35° puts the telescope within a
    /// degree of due west, which is one of the two directions where both bolts
    /// move the field the same way and neither can be measured. See
    /// <see cref="TrackingIsDegenerateDueEastAndWest"/>.
    /// </summary>
    private const double MechanicalRotation = -75.0;

    /// <summary>
    /// Where the telescope points for a given misalignment, with the mount
    /// mechanically frozen. Cone error is included throughout: it must not
    /// matter here either.
    /// </summary>
    private static HorizontalCoordinates Pointing(MountMisalignment misalignment, double coneArcminutes = 25.0) =>
        MountMechanics.Compose(Latitude, misalignment, MechanicalRotation, MechanicalDeclination, coneArcminutes, 40.0);

    private static HorizontalCoordinates Axis(MountMisalignment misalignment) =>
        MountForwardModel.MountAxis(Latitude, misalignment);

    [Fact]
    public void UntouchedBolts_ReportNoChange()
    {
        var start = new MountMisalignment(30.0, -25.0);
        var tracker = new AxisTracker(Latitude, Axis(start), Pointing(start));

        AxisUpdate update = tracker.Update(Pointing(start));

        Assert.True(update.IsReliable, update.UnreliableReason);
        Assert.Equal(0.0, update.AppliedAltitudeArcminutes, precision: 6);
        Assert.Equal(0.0, update.AppliedAzimuthArcminutes, precision: 6);
        Assert.Equal(start.AltitudeErrorArcminutes, update.AltitudeErrorArcminutes, precision: 5);
        Assert.Equal(start.AzimuthErrorArcminutes, update.AzimuthErrorArcminutes, precision: 5);
    }

    /// <summary>
    /// The core claim: a bolt turn is recovered from one field, and the axis
    /// estimate follows it. Swept over turns from an arcminute -- a nudge -- to a
    /// degree, which is a substantial handful of turns and well outside the
    /// small-angle regime.
    /// </summary>
    [Theory]
    [InlineData(1.0, 0.0)]
    [InlineData(0.0, 1.0)]
    [InlineData(-5.0, 3.0)]
    [InlineData(12.0, -9.0)]
    [InlineData(-30.0, -40.0)]
    [InlineData(60.0, 45.0)]
    public void BoltTurn_IsRecoveredFromASingleField(double altitudeTurn, double azimuthTurn)
    {
        var start = new MountMisalignment(35.0, -28.0);
        var tracker = new AxisTracker(Latitude, Axis(start), Pointing(start));

        var afterTurn = new MountMisalignment(
            start.AltitudeErrorArcminutes + altitudeTurn,
            start.AzimuthErrorArcminutes + azimuthTurn);

        AxisUpdate update = tracker.Update(Pointing(afterTurn));

        Assert.True(update.IsReliable, update.UnreliableReason);

        // The turn itself, which is what tells the user how much of the
        // correction they have actually put in.
        Assert.Equal(altitudeTurn, update.AppliedAltitudeArcminutes, precision: 3);
        Assert.Equal(azimuthTurn, update.AppliedAzimuthArcminutes, precision: 3);

        // And the resulting axis, which is what they are trying to zero.
        Assert.Equal(afterTurn.AltitudeErrorArcminutes, update.AltitudeErrorArcminutes, precision: 3);
        Assert.Equal(afterTurn.AzimuthErrorArcminutes, update.AzimuthErrorArcminutes, precision: 3);
    }

    /// <summary>
    /// Cone error must be irrelevant to the live readout as well as to the fit.
    /// </summary>
    [Theory]
    [InlineData(0.0)]
    [InlineData(30.0)]
    [InlineData(120.0)]
    public void ConeError_DoesNotAffectTracking(double coneArcminutes)
    {
        var start = new MountMisalignment(35.0, -28.0);
        var tracker = new AxisTracker(Latitude, Axis(start), Pointing(start, coneArcminutes));

        var afterTurn = new MountMisalignment(
            start.AltitudeErrorArcminutes - 14.0, start.AzimuthErrorArcminutes + 11.0);

        AxisUpdate update = tracker.Update(Pointing(afterTurn, coneArcminutes));

        Assert.True(update.IsReliable, update.UnreliableReason);
        Assert.Equal(-14.0, update.AppliedAltitudeArcminutes, precision: 3);
        Assert.Equal(11.0, update.AppliedAzimuthArcminutes, precision: 3);
    }

    /// <summary>
    /// Tracking is a rotation about the mount's own axis, so if it were mistaken
    /// for a bolt turn the readout would wander steadily while the user did
    /// nothing at all -- the most damaging possible failure for a live display.
    /// The drive's rotation is supplied and must be removed exactly.
    /// </summary>
    [Fact]
    public void SiderealTracking_IsNotMistakenForABoltTurn()
    {
        var start = new MountMisalignment(30.0, -25.0);
        var tracker = new AxisTracker(Latitude, Axis(start), Pointing(start));

        foreach (double driveRotation in new[] { 0.25, 1.0, 5.0, 15.0 })
        {
            // The mount has driven this far about its own axis; nothing else moved.
            HorizontalCoordinates moved = MountMechanics.Compose(
                Latitude, start, MechanicalRotation + driveRotation, MechanicalDeclination, 25.0, 40.0);

            AxisUpdate update = tracker.Update(moved, driveRotation);

            Assert.True(update.IsReliable, update.UnreliableReason);
            Assert.True(Math.Abs(update.AppliedAltitudeArcminutes) < 0.05,
                $"{driveRotation}° of tracking read as {update.AppliedAltitudeArcminutes:F3}' of altitude bolt");
            Assert.True(Math.Abs(update.AppliedAzimuthArcminutes) < 0.05,
                $"{driveRotation}° of tracking read as {update.AppliedAzimuthArcminutes:F3}' of azimuth bolt");
        }
    }

    /// <summary>
    /// Ignoring the drive's rotation must not be silently absorbed into a bogus
    /// bolt reading. Either the residual gives it away, or the inferred turn is
    /// visibly large -- what must not happen is a confident, wrong, small number.
    /// </summary>
    [Fact]
    public void TrackingIgnored_DoesNotProduceAQuietlyWrongReading()
    {
        var start = new MountMisalignment(30.0, -25.0);
        var tracker = new AxisTracker(Latitude, Axis(start), Pointing(start));

        HorizontalCoordinates moved = MountMechanics.Compose(
            Latitude, start, MechanicalRotation + 5.0, MechanicalDeclination, 25.0, 40.0);

        // Drive rotation not declared.
        AxisUpdate update = tracker.Update(moved);

        bool obviouslyWrong = !update.IsReliable
            || Math.Abs(update.AppliedAltitudeArcminutes) > 5.0
            || Math.Abs(update.AppliedAzimuthArcminutes) > 5.0;

        Assert.True(obviouslyWrong,
            $"undeclared tracking produced a plausible-looking reading: alt {update.AppliedAltitudeArcminutes:F2}', " +
            $"az {update.AppliedAzimuthArcminutes:F2}', residual {update.ResidualArcseconds:F1}\"");
    }

    /// <summary>
    /// A declination slip is absorbed as a bolt turn, silently, and cannot be
    /// otherwise. This test exists to pin that limitation down rather than to
    /// claim a capability: the two bolt parameters span every way a single field
    /// can move, so any motion fits exactly and the residual stays near zero.
    ///
    /// The swept measurement is the opposite case -- six points on a circle are
    /// heavily over-determined, which is why D11's residual check catches this
    /// there. So live tracking refines a trusted measurement; it cannot police
    /// one. What the user does get is the pair of inferred bolt turns, which is
    /// the practical safeguard: they know which bolts they actually touched.
    /// </summary>
    [Fact]
    public void DeclinationSlip_IsIndistinguishableFromABoltTurn()
    {
        var start = new MountMisalignment(30.0, -25.0);
        var tracker = new AxisTracker(Latitude, Axis(start), Pointing(start));

        HorizontalCoordinates slipped = MountMechanics.Compose(
            Latitude, start, MechanicalRotation, MechanicalDeclination + 2.0 / 60.0, 25.0, 40.0);

        AxisUpdate update = tracker.Update(slipped);

        // Fitted, not refused, and with a residual that reveals nothing.
        Assert.True(update.IsReliable);
        Assert.True(update.ResidualArcseconds < 1.0,
            $"residual {update.ResidualArcseconds:F3}\" -- a single field cannot expose this, by construction");

        // The tell is that it attributes movement to bolts the user did not turn.
        double attributed = Math.Abs(update.AppliedAltitudeArcminutes) + Math.Abs(update.AppliedAzimuthArcminutes);
        Assert.True(attributed > 0.5,
            "the slip should at least show up as bolt movement the user can recognise they did not make");
    }

    /// <summary>
    /// Even a gross displacement fits with a negligible residual, which is why
    /// this class carries no residual-based guard. The two bolt angles reach a
    /// two-parameter family of directions covering most of the sphere, so a
    /// residual test would pass always and read like a safety net while
    /// providing nothing. Pinned as a test so nobody adds one back believing it
    /// helps.
    /// </summary>
    [Fact]
    public void AnySingleFieldMotionFits_WhichIsWhyThereIsNoResidualGuard()
    {
        var start = new MountMisalignment(30.0, -25.0);
        HorizontalCoordinates reference = Pointing(start);
        var tracker = new AxisTracker(Latitude, Axis(start), reference);

        foreach (double azimuthShift in new[] { 5.0, 20.0, 40.0 })
        {
            var displaced = new HorizontalCoordinates(
                reference.AzimuthDegrees + azimuthShift,
                Math.Max(reference.AltitudeDegrees - azimuthShift / 2.0, 10.0));

            AxisUpdate update = tracker.Update(displaced);

            Assert.True(update.ResidualArcseconds < 1.0,
                $"a {azimuthShift}° displacement still fitted to {update.ResidualArcseconds:F4}\", " +
                "so no residual threshold could distinguish it from a bolt turn");
        }
    }

    /// <summary>
    /// The blind spot, which is not where intuition puts it. The altitude bolt
    /// turns the mount about an east-west horizontal axis, so pointing due east
    /// or due west puts the telescope in that axis' own vertical plane -- and
    /// there both bolts move the field along the same line. Recorded as a test
    /// because it is a real constraint on the feature, and because the first
    /// version of these tests accidentally sat on it.
    /// </summary>
    [Fact]
    public void TrackingIsDegenerateDueEastAndWest()
    {
        var start = new MountMisalignment(30.0, -25.0);
        HorizontalCoordinates axis = Axis(start);

        foreach (double altitude in new[] { 20.0, 45.0, 70.0 })
        {
            foreach (double degenerateAzimuth in new[] { 90.0, 270.0 })
            {
                var pointing = new HorizontalCoordinates(degenerateAzimuth, altitude);
                Assert.False(AxisTracker.IsUsableTrackingGeometry(axis, pointing),
                    $"azimuth {degenerateAzimuth} at altitude {altitude} should be refused");

                AxisUpdate update = new AxisTracker(Latitude, axis, pointing).Update(pointing);
                Assert.False(update.IsReliable);
                Assert.Contains("same direction", update.UnreliableReason!, StringComparison.OrdinalIgnoreCase);
            }

            // The meridian, by contrast, is the best place to be.
            foreach (double goodAzimuth in new[] { 0.0, 180.0 })
            {
                Assert.True(AxisTracker.IsUsableTrackingGeometry(axis, new HorizontalCoordinates(goodAzimuth, altitude)),
                    $"azimuth {goodAzimuth} at altitude {altitude} should be usable");
            }
        }
    }

    /// <summary>
    /// The zenith degrades the geometry too, but far more gently than the
    /// east-west locus -- worth distinguishing, because the advice to the user
    /// differs.
    /// </summary>
    [Fact]
    public void ZenithDegradesConditioning_MoreGentlyThanTheEastWestBlindSpot()
    {
        HorizontalCoordinates axis = Axis(new MountMisalignment(30.0, -25.0));

        double onMeridianLow = AxisTracker.ConditioningAt(axis, new HorizontalCoordinates(0.0, 20.0));
        double onMeridianHigh = AxisTracker.ConditioningAt(axis, new HorizontalCoordinates(0.0, 80.0));
        double dueWest = AxisTracker.ConditioningAt(axis, new HorizontalCoordinates(270.0, 20.0));

        Assert.True(onMeridianHigh > onMeridianLow, "higher altitude should condition worse");
        Assert.True(dueWest > 100.0 * onMeridianHigh,
            $"the east-west blind spot ({dueWest:F0}) should dwarf the zenith effect ({onMeridianHigh:F0})");
    }

    /// <summary>
    /// Successive turns must accumulate correctly, since that is how the feature
    /// is actually used: the user keeps turning while watching the number.
    /// </summary>
    [Fact]
    public void SuccessiveTurns_ConvergeTowardAlignment()
    {
        var current = new MountMisalignment(90.0, -75.0);
        var tracker = new AxisTracker(Latitude, Axis(current), Pointing(current));

        var trail = new List<double>();
        for (int step = 0; step < 6; step++)
        {
            AxisUpdate update = tracker.Update(Pointing(current));
            Assert.True(update.IsReliable, update.UnreliableReason);
            trail.Add(update.TotalErrorArcminutes);

            // The user turns each bolt a third of the way toward zero, guided
            // only by what the tracker reports.
            current = new MountMisalignment(
                current.AltitudeErrorArcminutes - update.AltitudeErrorArcminutes / 3.0,
                current.AzimuthErrorArcminutes - update.AzimuthErrorArcminutes / 3.0);
        }

        for (int i = 1; i < trail.Count; i++)
        {
            Assert.True(trail[i] < trail[i - 1],
                $"step {i} did not improve: {string.Join(" -> ", trail.Select(t => $"{t:F2}'"))}");
        }

        Assert.True(trail[^1] < 0.25 * trail[0],
            $"six guided turns should have cut the error substantially: {trail[0]:F2}' -> {trail[^1]:F2}'");
    }

    /// <summary>
    /// Southern hemisphere: the axis points at the south celestial pole, so the
    /// signs of both bolt figures are the ones that would be easiest to get
    /// backwards.
    /// </summary>
    [Theory]
    [InlineData(-33.9)]
    [InlineData(-55.0)]
    public void SouthernHemisphere_TracksWithTheSameSigns(double latitude)
    {
        var start = new MountMisalignment(25.0, -20.0);
        HorizontalCoordinates axis = MountForwardModel.MountAxis(latitude, start);
        HorizontalCoordinates reference = MountMechanics.Compose(latitude, start, 35.0, -40.0, 25.0, 40.0);

        var tracker = new AxisTracker(latitude, axis, reference);

        var afterTurn = new MountMisalignment(
            start.AltitudeErrorArcminutes - 10.0, start.AzimuthErrorArcminutes + 8.0);
        HorizontalCoordinates moved = MountMechanics.Compose(latitude, afterTurn, 35.0, -40.0, 25.0, 40.0);

        AxisUpdate update = tracker.Update(moved);

        Assert.True(update.IsReliable, update.UnreliableReason);
        Assert.Equal(-10.0, update.AppliedAltitudeArcminutes, precision: 3);
        Assert.Equal(8.0, update.AppliedAzimuthArcminutes, precision: 3);
        Assert.Equal(afterTurn.AltitudeErrorArcminutes, update.AltitudeErrorArcminutes, precision: 3);
        Assert.Equal(afterTurn.AzimuthErrorArcminutes, update.AzimuthErrorArcminutes, precision: 3);
    }
}
