using FreePolarAlign.Core.Alignment;
using FreePolarAlign.Core.Astrometry;
using FreePolarAlign.Devices;
using FreePolarAlign.Devices.Simulated;
using FreePolarAlign.Tests.Support;
using Xunit;

namespace FreePolarAlign.Tests.Devices;

/// <summary>
/// The simulated mount's motion, which the event-triggered capture of D26 is
/// tested against: a slew that takes time, reports itself, and passes through
/// the positions in between.
///
/// Every mount here is perfectly aligned and has no cone error, so the
/// mechanical angles can be read straight back off where the telescope
/// physically points. That is what makes a position part-way through a slew
/// checkable at all.
///
/// Time is the test's (D37): a slew is watched by moving the fake clock, so a
/// position half-way through is exactly half-way, not roughly.
/// </summary>
public class SimulatedMountMotionTests
{
    private static readonly GeodeticLocation Site = new(45.0, 15.0, 200.0);
    private static readonly ObserverSite Observer = new(45.0, 15.0, 200.0);

    private static readonly TimeSpan Slew = TimeSpan.FromSeconds(2);

    /// <summary>
    /// A slew's target is resolved for the instant it arrives, so a "40°" slew
    /// also covers the sky's turn while it lasts, and the angles move linearly
    /// over the whole of it. Half-way is therefore 20° plus one second of
    /// sidereal rate (0.0042°), exactly.
    /// </summary>
    private static readonly double HalfWay = 20.0 + Slew.TotalSeconds / 2.0 * 360.0 / 86164.0905;

    private readonly TestClock _clock = new();

    private async Task<SimulatedMount> ConnectedAsync(TimeSpan slewDuration)
    {
        var mount = new SimulatedMount(
            new SimulatedMountOptions(Site, MountMisalignment.Aligned, SlewDuration: slewDuration), _clock);
        await mount.ConnectAsync();
        return mount;
    }

    private static (double Rotation, double Declination) MechanicsAt(SimulatedMount mount, DateTime utc) =>
        MountMechanics.Decompose(Site.LatitudeDegrees, mount.PhysicalPointingAt(utc));

    /// <summary>
    /// The coordinates to command for given mechanical angles, resolved the
    /// way the session resolves them (D16), so a test can ask for a pure
    /// rotation and know the declination should not move.
    /// </summary>
    private (double Ra, double Dec) Resolve(double rotation, double declination)
    {
        HorizontalCoordinates ideal = MountMechanics.Compose(
            Site.LatitudeDegrees, MountMisalignment.Aligned, rotation, declination);
        return TopocentricConverter.FromAltAz(ideal, _clock.UtcNow, Observer, AtmosphericConditions.Vacuum);
    }

    private static double RotationDifference(double to, double from)
    {
        double difference = to - from;
        return difference - 360.0 * Math.Round(difference / 360.0);
    }

    /// <summary>
    /// A slew reports itself while it lasts and passes through the positions in
    /// between, in what the mount reports and in where it physically points.
    /// This is what lets the engine tell that a frame was exposed during motion.
    /// Against the old simulator, which waited out the slew and then jumped,
    /// the mount reported no motion and sat at its start throughout.
    /// </summary>
    [Fact]
    public async Task A_slew_takes_its_duration_and_passes_through_the_positions_between()
    {
        using SimulatedMount mount = await ConnectedAsync(Slew);
        (double startRotation, double declination) = MechanicsAt(mount, _clock.UtcNow);
        (double ra, double dec) = Resolve(startRotation + 40.0, declination);

        Task slew = mount.SlewToCoordinatesAsync(ra, dec);
        await _clock.AdvanceAsync(Slew / 2);

        MountPosition during = await mount.GetPositionAsync();
        Assert.True(during.IsSlewing);
        Assert.False(slew.IsCompleted);

        (double rotation, double midDeclination) = MechanicsAt(mount, during.TimestampUtc);
        double travelled = RotationDifference(rotation, startRotation);
        Assert.Equal(HalfWay, travelled, 1e-6);

        // The angles are interpolated, not the sky coordinates: a pure
        // rotation keeps the mechanical declination exactly where it was.
        Assert.Equal(declination, midDeclination, 1e-3);

        // What it reports is where its encoders are, not where it was told to go.
        (double reportedRotation, _) = MountMechanics.Decompose(
            Site.LatitudeDegrees,
            TopocentricConverter.ToAltAz(during.RaDegrees, during.DecDegrees, during.TimestampUtc, Observer, AtmosphericConditions.Vacuum));
        Assert.Equal(rotation, reportedRotation, 1e-6);

        await _clock.AdvanceAsync(Slew / 2);
        await slew;

        MountPosition after = await mount.GetPositionAsync();
        Assert.False(after.IsSlewing);
        Assert.Equal(ra, after.RaDegrees, 1e-9);
        Assert.Equal(dec, after.DecDegrees, 1e-9);
    }

    /// <summary>
    /// With no slew duration configured, as every existing test has it, a slew
    /// completes before the call returns and is never seen in progress.
    /// </summary>
    [Fact]
    public async Task With_no_slew_duration_a_slew_is_instant_and_never_reported_as_slewing()
    {
        using SimulatedMount mount = await ConnectedAsync(TimeSpan.Zero);
        (double rotation, double declination) = MechanicsAt(mount, _clock.UtcNow);
        (double ra, double dec) = Resolve(rotation + 30.0, declination);

        Task slew = mount.SlewToCoordinatesAsync(ra, dec);
        Assert.True(slew.IsCompletedSuccessfully);

        MountPosition after = await mount.GetPositionAsync();
        Assert.False(after.IsSlewing);
        Assert.Equal(ra, after.RaDegrees, 1e-9);
        Assert.Equal(dec, after.DecDegrees, 1e-9);
    }

    /// <summary>
    /// A cancelled slew leaves the mount where it had got to, still, and
    /// believing it is there -- an aborted slew on real hardware stops the
    /// motors, it does not return them. The old simulator had not moved at all
    /// by then, so the mount was still at its start.
    /// </summary>
    [Fact]
    public async Task A_cancelled_slew_stops_where_it_had_got_to()
    {
        using SimulatedMount mount = await ConnectedAsync(Slew);
        (double startRotation, double declination) = MechanicsAt(mount, _clock.UtcNow);
        (double ra, double dec) = Resolve(startRotation + 40.0, declination);

        using var cancellation = new CancellationTokenSource();
        Task slew = mount.SlewToCoordinatesAsync(ra, dec, cancellation.Token);
        await _clock.AdvanceAsync(Slew / 2);
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => slew);

        MountPosition stopped = await mount.GetPositionAsync();
        Assert.False(stopped.IsSlewing);

        (double stoppedRotation, _) = MechanicsAt(mount, stopped.TimestampUtc);
        Assert.Equal(HalfWay, RotationDifference(stoppedRotation, startRotation), 1e-6);

        // Only tracking moves it from here, in sky coordinates not at all.
        await _clock.AdvanceAsync(TimeSpan.FromMilliseconds(300));
        MountPosition later = await mount.GetPositionAsync();
        Assert.Equal(stopped.RaDegrees, later.RaDegrees, 1e-9);
        Assert.Equal(stopped.DecDegrees, later.DecDegrees, 1e-9);
    }

    /// <summary>
    /// A slew started during another replaces it from wherever the mount has
    /// got to, as a hand controller's does, and the replaced call returns only
    /// once the mount has stopped -- as waiting on a real driver's
    /// <c>Slewing</c> would. The old simulator had each slew wait out its own
    /// duration and then jump to its own target, so the first one to finish
    /// won regardless of which was commanded last.
    /// </summary>
    [Fact]
    public async Task A_slew_during_a_slew_replaces_it_from_where_the_mount_has_got_to()
    {
        using SimulatedMount mount = await ConnectedAsync(Slew);
        (double startRotation, double declination) = MechanicsAt(mount, _clock.UtcNow);
        (double firstRa, double firstDec) = Resolve(startRotation + 40.0, declination);
        (double secondRa, double secondDec) = Resolve(startRotation - 20.0, declination);

        Task first = mount.SlewToCoordinatesAsync(firstRa, firstDec);
        await _clock.AdvanceAsync(Slew / 2);

        (double reached, _) = MechanicsAt(mount, _clock.UtcNow);
        Task second = mount.SlewToCoordinatesAsync(secondRa, secondDec);

        // Continues from where the first had got to, rather than from its
        // start or its target. The clock has not moved, so exactly from there.
        (double continuedFrom, _) = MechanicsAt(mount, _clock.UtcNow);
        Assert.Equal(HalfWay, RotationDifference(reached, startRotation), 1e-6);
        Assert.Equal(reached, continuedFrom, 1e-6);

        // Past the first slew's own end, and short of the second's.
        await _clock.AdvanceAsync(Slew * 0.75);
        Assert.False(first.IsCompleted);
        Assert.True((await mount.GetPositionAsync()).IsSlewing);

        await _clock.AdvanceAsync(Slew);
        await Task.WhenAll(first, second);

        MountPosition after = await mount.GetPositionAsync();
        Assert.False(after.IsSlewing);
        Assert.Equal(secondRa, after.RaDegrees, 1e-9);
        Assert.Equal(secondDec, after.DecDegrees, 1e-9);
    }

    /// <summary>
    /// A declination press moves the mechanical declination and nothing else,
    /// and the reported coordinates follow it, which is the signal the engine
    /// uses to restart a sequence whose declination has been broken (D11).
    /// </summary>
    [Fact]
    public async Task A_declination_nudge_moves_the_mechanical_declination_and_is_reported()
    {
        using SimulatedMount mount = await ConnectedAsync(TimeSpan.Zero);
        DateTime at = _clock.UtcNow;
        (double rotationBefore, double declinationBefore) = MechanicsAt(mount, at);
        MountPosition before = await mount.GetPositionAsync();

        mount.NudgeDeclination(10.0);

        (double rotationAfter, double declinationAfter) = MechanicsAt(mount, at);
        Assert.Equal(rotationBefore, rotationAfter, 1e-6);
        Assert.Equal(10.0, (declinationAfter - declinationBefore) * 60.0, 1e-4);

        // Mechanical and sky declination are not the same thing on a mount of
        // date commanded in J2000 (D16), but within a fraction of an arcminute.
        MountPosition after = await mount.GetPositionAsync();
        Assert.InRange((after.DecDegrees - before.DecDegrees) * 60.0, 9.5, 10.5);
    }

    /// <summary>
    /// A nudge during a slew carries through to where it stops, rather than
    /// being quietly undone when the slew arrives at the declination it was
    /// originally sent to.
    /// </summary>
    [Fact]
    public async Task A_declination_nudge_during_a_slew_survives_its_arrival()
    {
        using SimulatedMount mount = await ConnectedAsync(Slew);
        (double startRotation, double declination) = MechanicsAt(mount, _clock.UtcNow);
        (double ra, double dec) = Resolve(startRotation + 20.0, declination);

        Task slew = mount.SlewToCoordinatesAsync(ra, dec);
        await _clock.AdvanceAsync(Slew / 4);
        mount.NudgeDeclination(-6.0);
        await _clock.AdvanceAsync(Slew);
        await slew;

        (_, double arrived) = MechanicsAt(mount, _clock.UtcNow);
        Assert.Equal(-6.0, (arrived - declination) * 60.0, 1e-3);
    }

    /// <summary>
    /// With the drive off, a mount's encoders hold still and the sky moves past,
    /// so the RA it reports drifts at the sidereal rate while the telescope
    /// stays put. The simulator once went on reporting the RA it was last sent
    /// to, which is what a *driven* mount reports: a live reading that takes
    /// the drive's turn from the report (D29) would have read that as tracking.
    /// </summary>
    [Fact]
    public async Task AnUndrivenMount_ReportsAnRAThatDriftsWithTheSky()
    {
        var mount = new SimulatedMount(new SimulatedMountOptions(Site, MountMisalignment.Aligned, Tracking: false), _clock);
        await mount.ConnectAsync();

        MountPosition before = await mount.GetPositionAsync();
        await _clock.AdvanceAsync(TimeSpan.FromSeconds(1));
        MountPosition after = await mount.GetPositionAsync();

        double seconds = (after.TimestampUtc - before.TimestampUtc).TotalSeconds;
        Assert.Equal(1.0, seconds);
        double driftArcseconds = RotationDifference(after.RaDegrees, before.RaDegrees) * 3600.0;

        // A fixed hour angle under a turning sky: RA rises with sidereal time,
        // 15.04" of it per second.
        Assert.Equal(15.041 * seconds, driftArcseconds, tolerance: 0.5);
        Assert.Equal(
            MechanicsAt(mount, before.TimestampUtc).Rotation,
            MechanicsAt(mount, after.TimestampUtc).Rotation,
            precision: 6);
    }
}
