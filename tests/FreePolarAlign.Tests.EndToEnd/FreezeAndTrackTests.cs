using FreePolarAlign.Core.Alignment;
using FreePolarAlign.Core.Astrometry;
using FreePolarAlign.Core.Engine;
using FreePolarAlign.Session;
using Xunit;

namespace FreePolarAlign.Tests.EndToEnd;

/// <summary>
/// D29: once the sweep is finished the session keeps solving every frame and
/// follows the bolts. Run against the real mount mechanics with a noiseless
/// solver (<see cref="SessionHarness"/>), so the truth to compare against is
/// the simulated mount's own axis -- which is moved here exactly as a user
/// turning the bolts would move it.
/// </summary>
public class FreezeAndTrackTests
{
    /// <summary>
    /// Well under what the success indication cares about, and well over what a
    /// noiseless solver and a five-point sweep leave behind.
    /// </summary>
    private const double ToleranceArcminutes = 0.5;

    /// <summary>
    /// A bolt reading that should be zero. Sidereal drive left out of the
    /// reckoning puts fifteen arcseconds a second into it, which this catches
    /// within a second or two.
    /// </summary>
    private const double StillToleranceArcminutes = 0.1;

    private static async Task<AlignmentEstimate> CompleteConnectedSweepAsync(SessionHarness harness)
    {
        await harness.ReadyAsync();

        for (int point = 1; point <= 5; point++)
        {
            await harness.WaitForSampleAsync(point);
            if (point < 5)
            {
                await harness.Recorder.WaitForAsync<SlewProposedEvent>(e => e.PointIndex == point + 1);
                await harness.Session.SendAsync(new CaptureNextPointCommand());
            }
        }

        await harness.Recorder.WaitForAsync<TrackingStartedEvent>();
        return harness.Recorder.Last<AlignmentUpdatedEvent>()!.Estimate;
    }

    private static async Task<AlignmentEstimate> CompleteUnconnectedSweepAsync(SessionHarness harness)
    {
        await harness.ConnectAsync(mount: false);
        await harness.ConfirmSiteAsync();
        await harness.StartAsync();

        double declination = harness.Recorder.Last<TargetSelectedEvent>()!.DeclinationDegrees;

        for (int point = 1; point <= 5; point++)
        {
            SlewProposedEvent instruction = await harness.Recorder.WaitForAsync<SlewProposedEvent>(e => e.PointIndex == point);
            await harness.MoveFromOutsideAsync(instruction.MechanicalRotationDegrees, declination);
            await harness.WaitForSampleAsync(point, poll: false);
        }

        await harness.Recorder.WaitForAsync<TrackingStartedEvent>();
        return harness.Recorder.Last<AlignmentUpdatedEvent>()!.Estimate;
    }

    /// <summary>
    /// A live reading wholly from after <paramref name="afterUtc"/>. The first
    /// one published after it is skipped, because its solve may have looked at
    /// the sky a moment before.
    /// </summary>
    private static async Task<AlignmentTrackedEvent> NextReadingAsync(
        SessionHarness harness, DateTimeOffset afterUtc, bool poll = true)
    {
        AlignmentTrackedEvent straddling = await harness.Recorder.WaitForAsync<AlignmentTrackedEvent>(
            e => e.TimestampUtc > afterUtc,
            poll: poll ? harness.PollAsync : null,
            timeout: TimeSpan.FromSeconds(30));

        return await harness.Recorder.WaitForAsync<AlignmentTrackedEvent>(
            e => e.TimestampUtc > straddling.TimestampUtc && e.IsReliable,
            poll: poll ? harness.PollAsync : null,
            timeout: TimeSpan.FromSeconds(30));
    }

    /// <summary>What is actually left to correct on the simulated mount: the truth the reading must match.</summary>
    private static MountMisalignment Truth(SessionHarness harness) => harness.Simulated.CurrentMisalignment;

    private static double TrueTotalArcminutes(SessionHarness harness)
    {
        HorizontalCoordinates axis = harness.Simulated.TruePolarAxis;
        HorizontalCoordinates pole = MountForwardModel.NominalPole(SessionHarness.Latitude);
        double d2r = Math.PI / 180.0;
        double cos = Math.Sin(axis.AltitudeDegrees * d2r) * Math.Sin(pole.AltitudeDegrees * d2r) +
                     Math.Cos(axis.AltitudeDegrees * d2r) * Math.Cos(pole.AltitudeDegrees * d2r) *
                     Math.Cos((axis.AzimuthDegrees - pole.AzimuthDegrees) * d2r);
        return Math.Acos(Math.Min(1.0, cos)) / d2r * 60.0;
    }

    // ---- The hand-over ----

    /// <summary>
    /// The sweep does not end the session any more: it hands over to the live
    /// reading, which starts from where the sweep left the axis.
    /// </summary>
    [Fact]
    public async Task AFinishedSweep_HandsOverToALiveReadingThatStartsWhereItLeftOff()
    {
        using var harness = new SessionHarness();
        AlignmentEstimate sweep = await CompleteConnectedSweepAsync(harness);

        Assert.True(harness.Recorder.Last<TrackingStartedEvent>()!.IsUsableGeometry, harness.Recorder.Trail());
        Assert.Null(harness.Recorder.Last<SessionCompletedEvent>());

        AlignmentTrackedEvent reading = await NextReadingAsync(harness, DateTimeOffset.UtcNow);

        Assert.Equal(sweep.AltitudeErrorArcminutes, reading.Estimate.AltitudeErrorArcminutes, tolerance: StillToleranceArcminutes);
        Assert.Equal(sweep.AzimuthErrorArcminutes, reading.Estimate.AzimuthErrorArcminutes, tolerance: StillToleranceArcminutes);
        Assert.Equal(0.0, reading.AppliedAltitudeArcminutes, tolerance: StillToleranceArcminutes);
        Assert.Equal(0.0, reading.AppliedAzimuthArcminutes, tolerance: StillToleranceArcminutes);

        // The sweep's uncertainty carries over: a single solve adds nothing a
        // fraction of an arcminute would notice.
        Assert.Equal(sweep.TotalSigmaArcminutes, reading.Estimate.TotalSigmaArcminutes);
    }

    /// <summary>
    /// The feature itself: turn the bolts by what the reading says, and the
    /// reading follows them down to nothing -- checked against the simulated
    /// mount's true axis, not against the reading's own arithmetic.
    /// </summary>
    [Fact]
    public async Task TurningTheBoltsAsTold_IsFollowedToAlignment()
    {
        using var harness = new SessionHarness();
        AlignmentEstimate sweep = await CompleteConnectedSweepAsync(harness);

        // Half of it first, as a user would, then the rest.
        harness.Simulated.AdjustAxis(-sweep.AltitudeErrorArcminutes / 2.0, -sweep.AzimuthErrorArcminutes / 2.0);
        AlignmentTrackedEvent halfway = await NextReadingAsync(harness, DateTimeOffset.UtcNow);

        Assert.Equal(Truth(harness).AltitudeErrorArcminutes, halfway.Estimate.AltitudeErrorArcminutes, tolerance: ToleranceArcminutes);
        Assert.Equal(Truth(harness).AzimuthErrorArcminutes, halfway.Estimate.AzimuthErrorArcminutes, tolerance: ToleranceArcminutes);
        Assert.Equal(-sweep.AltitudeErrorArcminutes / 2.0, halfway.AppliedAltitudeArcminutes, tolerance: ToleranceArcminutes);
        Assert.Equal(-sweep.AzimuthErrorArcminutes / 2.0, halfway.AppliedAzimuthArcminutes, tolerance: ToleranceArcminutes);

        harness.Simulated.AdjustAxis(-halfway.Estimate.AltitudeErrorArcminutes, -halfway.Estimate.AzimuthErrorArcminutes);
        AlignmentTrackedEvent done = await NextReadingAsync(harness, DateTimeOffset.UtcNow);

        Assert.True(done.Estimate.TotalErrorArcminutes < ToleranceArcminutes,
            $"the reading ended at {done.Estimate.TotalErrorArcminutes:F2}'{harness.Recorder.Trail()}");
        Assert.True(TrueTotalArcminutes(harness) < ToleranceArcminutes,
            $"following the reading left {TrueTotalArcminutes(harness):F2}' of real error");
    }

    // ---- What is not a bolt turn ----

    /// <summary>
    /// Standing still, the reading stands still, drive on or off. The drive
    /// turns the telescope about the axis, and a reading that took that for a
    /// bolt turn would wander by fifteen arcseconds a second while the user did
    /// nothing -- which is also why the simulated mount now reports the drifting
    /// RA of an undriven mount, as a real one does.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Connected_StandingStill_TheReadingStandsStill(bool driveRunning)
    {
        using var harness = new SessionHarness(tracking: driveRunning);
        AlignmentEstimate sweep = await CompleteConnectedSweepAsync(harness);

        await Task.Delay(TimeSpan.FromSeconds(3));
        AlignmentTrackedEvent later = await NextReadingAsync(harness, DateTimeOffset.UtcNow);

        Assert.True(Math.Abs(later.AppliedAltitudeArcminutes) < StillToleranceArcminutes &&
                    Math.Abs(later.AppliedAzimuthArcminutes) < StillToleranceArcminutes,
            $"with nothing touched the bolts read {later.AppliedAltitudeArcminutes:F2}' and " +
            $"{later.AppliedAzimuthArcminutes:F2}'");
        Assert.Equal(sweep.TotalErrorArcminutes, later.Estimate.TotalErrorArcminutes, tolerance: StillToleranceArcminutes);
    }

    /// <summary>
    /// A connected mount can be moved in RA while the bolts are turned -- to get
    /// away from due east or west, say (D17) -- because it reports how far it
    /// turned, and that turn is taken out rather than read as a bolt.
    /// </summary>
    [Fact]
    public async Task Connected_AnRAMoveFromTheHandController_IsNotABoltTurn()
    {
        using var harness = new SessionHarness();
        AlignmentEstimate sweep = await CompleteConnectedSweepAsync(harness);

        await harness.MoveFromOutsideAsync(harness.MountRotationNow() - 15.0);
        harness.Simulated.AdjustAxis(-5.0, 4.0);
        AlignmentTrackedEvent reading = await NextReadingAsync(harness, DateTimeOffset.UtcNow);

        Assert.Equal(-5.0, reading.AppliedAltitudeArcminutes, tolerance: ToleranceArcminutes);
        Assert.Equal(4.0, reading.AppliedAzimuthArcminutes, tolerance: ToleranceArcminutes);
        Assert.Equal(Truth(harness).AltitudeErrorArcminutes, reading.Estimate.AltitudeErrorArcminutes, tolerance: ToleranceArcminutes);
        Assert.Equal(Truth(harness).AzimuthErrorArcminutes, reading.Estimate.AzimuthErrorArcminutes, tolerance: ToleranceArcminutes);
        Assert.Empty(harness.Mount.Slews.Skip(4));
    }

    /// <summary>
    /// A declination move is the one thing a single field cannot tell from a
    /// bolt turn (D17), and a connected mount reports it, so it ends the live
    /// reading and a new sweep starts where the mount now is.
    /// </summary>
    [Fact]
    public async Task Connected_ADeclinationMove_EndsTheLiveReadingAndStartsANewSweep()
    {
        using var harness = new SessionHarness();
        await CompleteConnectedSweepAsync(harness);
        await NextReadingAsync(harness, DateTimeOffset.UtcNow);

        harness.Simulated.NudgeDeclination(3.0);
        SequenceRestartedEvent restarted = await harness.Recorder.WaitForAsync<SequenceRestartedEvent>(poll: harness.PollAsync);

        Assert.Contains("live reading", restarted.Reason, StringComparison.OrdinalIgnoreCase);

        // The restart is a sweep again: it proposes its first point, and no
        // live reading follows it.
        int from = harness.Recorder.IndexOfLast<SequenceRestartedEvent>();
        await harness.Recorder.WaitForAsync<SlewProposedEvent>(e => e.PointIndex == 1, fromIndex: from);
        await Task.Delay(300);
        Assert.DoesNotContain(harness.Recorder.Events.Skip(from), e => e is AlignmentTrackedEvent);
    }

    /// <summary>
    /// Nothing is sampled after the sweep, so a forced sample would only be
    /// silently ignored. It is refused, and says what to do instead.
    /// </summary>
    [Fact]
    public async Task RecordingASample_WhileLive_IsRefused()
    {
        using var harness = new SessionHarness();
        await CompleteConnectedSweepAsync(harness);

        await harness.Session.SendAsync(new RecordSampleCommand());

        Assert.Contains("new sequence", harness.Recorder.Last<CommandRejectedEvent>()!.Reason, StringComparison.Ordinal);
    }

    // ---- Without a mount ----

    /// <summary>
    /// With no mount to say how far the drive has turned, the frames have to
    /// show whether it is running: the reading waits for that, holding still,
    /// and then follows a bolt turn with the drive on or off. Both, because the
    /// sign of the drive's turn is what gets this wrong, and only a drive that is
    /// running exercises it.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Unconnected_FindsWhetherTheDriveRunsThenFollowsTheBolts(bool driveRunning)
    {
        using var harness = new SessionHarness(withMount: false, tracking: driveRunning);
        AlignmentEstimate sweep = await CompleteUnconnectedSweepAsync(harness);

        // Held still until the drive is known: the first reading comes only then.
        AlignmentTrackedEvent first = await NextReadingAsync(harness, DateTimeOffset.UtcNow, poll: false);
        Assert.Equal(sweep.TotalErrorArcminutes, first.Estimate.TotalErrorArcminutes, tolerance: StillToleranceArcminutes);

        harness.Simulated.AdjustAxis(-sweep.AltitudeErrorArcminutes, -sweep.AzimuthErrorArcminutes);
        await Task.Delay(TimeSpan.FromSeconds(2));
        AlignmentTrackedEvent after = await NextReadingAsync(harness, DateTimeOffset.UtcNow, poll: false);

        Assert.True(after.Estimate.TotalErrorArcminutes < ToleranceArcminutes,
            $"drive {(driveRunning ? "on" : "off")}: the reading ended at {after.Estimate.TotalErrorArcminutes:F2}' " +
            $"with bolts read as {after.AppliedAltitudeArcminutes:F2}' and {after.AppliedAzimuthArcminutes:F2}'");
        Assert.True(TrueTotalArcminutes(harness) < ToleranceArcminutes);
    }
}
