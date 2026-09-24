using FreePolarAlign.Core.Alignment;
using FreePolarAlign.Core.Engine;
using FreePolarAlign.Devices;
using FreePolarAlign.Devices.Simulated;
using FreePolarAlign.Devices.Simulated.SyntheticSky;
using FreePolarAlign.Session;
using Xunit;

namespace FreePolarAlign.Tests.EndToEnd;

/// <summary>
/// D18: the mount stands still until it is told to move, and an override that
/// changes declination restarts the sequence rather than corrupting it.
///
/// These run against the real mount mechanics with a noiseless solver (see
/// <see cref="SessionHarness"/>), so they need no quad database and any
/// discrepancy in a commanded coordinate is unambiguous rather than lost in
/// solve noise. What they do *not* test is accuracy; the exit-criterion tests
/// do that, with a real solver on rendered frames.
/// </summary>
public class ConfirmBeforeSlewTests
{
    /// <summary>A tenth of an arcminute -- far below anything that matters here, and far above float noise.</summary>
    private const double ToleranceDegrees = 0.1 / 60.0;

    private static readonly GeodeticLocation Site = SessionHarness.Site;

    // ---- Nothing moves unbidden ----

    /// <summary>
    /// The whole of D18 in one assertion. Starting a sequence plans it and says
    /// what it would do; it does not move the telescope. A user who presses
    /// "start" while the dew shield is still on, or while someone is standing
    /// under the counterweight, has to be given the chance to stop.
    /// </summary>
    [Fact]
    public async Task StartingASequence_MovesNothing()
    {
        using var harness = new SessionHarness(initialRotationDegrees: 12.0);

        await harness.ReadyAsync();
        await Task.Delay(200);

        Assert.Empty(harness.Mount.Slews);

        SlewProposedEvent? proposed = harness.Recorder.Last<SlewProposedEvent>();
        Assert.True(proposed is not null, harness.Recorder.Trail());
        Assert.Equal(1, proposed!.PointIndex);
    }

    /// <summary>
    /// Where the telescope already points somewhere usable east of the meridian,
    /// the first sample is taken there, without a click and without motion (D18
    /// as revised) -- which also measures the scale before anything moves, the
    /// one frame most worth having before a slew.
    /// </summary>
    [Fact]
    public async Task AnAnchoredFirstPoint_IsSampledWhereItStandsWithoutAClick()
    {
        using var harness = new SessionHarness();
        await harness.ReadyAsync();

        Assert.False(harness.Recorder.Last<SlewProposedEvent>()!.RequiresMotion);

        await harness.WaitForSampleAsync(1);

        Assert.Empty(harness.Mount.Slews);
    }

    /// <summary>
    /// Sequences start east and sweep west (D18 as revised). A telescope west of
    /// the meridian is proposed a move east first rather than anchored where it
    /// is -- and the move is only proposed.
    /// </summary>
    [Fact]
    public async Task WestOfTheMeridian_TheFirstPointIsAProposedMoveEast()
    {
        using var harness = new SessionHarness(initialRotationDegrees: 12.0);
        await harness.ReadyAsync();
        await Task.Delay(200);

        SlewProposedEvent proposed = harness.Recorder.Last<SlewProposedEvent>()!;
        Assert.True(proposed.RequiresMotion, harness.Recorder.Trail());
        Assert.True(proposed.MechanicalRotationDegrees < 0.0, $"proposed {proposed.MechanicalRotationDegrees:F1}°, which is not east");
        Assert.Empty(harness.Mount.Slews);
        Assert.Null(harness.Recorder.Last<PointCapturedEvent>());
    }

    /// <summary>
    /// Every later point is proposed and then waited on. The engine does not
    /// chain one sample into the next slew, however obvious the next step is.
    /// </summary>
    [Fact]
    public async Task EveryLaterPoint_WaitsToBeConfirmed()
    {
        using var harness = new SessionHarness();
        await harness.ReadyAsync();
        await harness.WaitForSampleAsync(1);

        SlewProposedEvent proposed = await harness.Recorder.WaitForAsync<SlewProposedEvent>(e => e.PointIndex == 2);
        Assert.True(proposed.RequiresMotion);

        await Task.Delay(200);
        Assert.Empty(harness.Mount.Slews);
        Assert.Single(harness.Recorder.All<PointCapturedEvent>());

        await harness.Session.SendAsync(new CaptureNextPointCommand());
        await harness.WaitForSampleAsync(2);

        Assert.Single(harness.Mount.Slews);
    }

    /// <summary>
    /// Confirming the proposal unedited must still hold the mechanical
    /// declination, which means re-resolving the coordinates for the moment of
    /// the slew rather than sending the ones displayed with the suggestion. A
    /// fixed sky coordinate does not hold a fixed mechanical declination as the
    /// sky turns, and the drift is arcminutes across a wide sweep (D16) -- large
    /// enough to invalidate the fit it is part of.
    /// </summary>
    [Fact]
    public async Task ConfirmingEveryProposal_HoldsTheMechanicalDeclinationAndCompletes()
    {
        using var harness = new SessionHarness();
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

        await harness.Recorder.WaitForAsync<SessionCompletedEvent>();
        Assert.Equal(4, harness.Mount.Slews.Count);

        double planned = SessionHarness.MechanicalDeclinationOf(harness.Mount.Slews[0].Ra, harness.Mount.Slews[0].Dec);
        foreach ((double ra, double dec) in harness.Mount.Slews)
        {
            Assert.Equal(planned, SessionHarness.MechanicalDeclinationOf(ra, dec), tolerance: ToleranceDegrees);
        }

        // Five points over 60°, all trusted, from a noiseless solver: the
        // injected misalignment comes back.
        AlignmentEstimate estimate = harness.Recorder.Last<AlignmentUpdatedEvent>()!.Estimate;
        Assert.Equal(SessionHarness.Injected.AltitudeErrorArcminutes, estimate.AltitudeErrorArcminutes, tolerance: 0.5);
        Assert.Equal(SessionHarness.Injected.AzimuthErrorArcminutes, estimate.AzimuthErrorArcminutes, tolerance: 0.5);
    }

    // ---- Overriding ----

    /// <summary>
    /// Moving along the same arc is an ordinary override: the user can see a tree
    /// where the engine cannot. The sequence continues, and the declination is
    /// snapped back to the sequence's exactly -- because the few arcminutes a
    /// hand-typed declination would otherwise sit out by is real declination
    /// movement, and the sequence must not contain any.
    ///
    /// The override here lands only 10° from the first sample, inside D27's
    /// spacing. It is sampled anyway: the spacing rule is for motion the engine
    /// did not command, and a slew the user confirmed to a position they chose is
    /// the opposite of that.
    /// </summary>
    [Fact]
    public async Task AnOverrideAlongTheSameArc_KeepsTheSequenceAndSnapsTheDeclination()
    {
        using var harness = new SessionHarness();
        await harness.ReadyAsync();
        await harness.WaitForSampleAsync(1);

        SlewProposedEvent proposal = await harness.Recorder.WaitForAsync<SlewProposedEvent>(e => e.PointIndex == 2);
        double sampleDeclination = harness.MechanicalDeclinationNow();

        // Five degrees back east of the proposal, along the same declination.
        await harness.Session.SendAsync(new ConfirmSlewCommand(
            (proposal.RaDegrees + 5.0) % 360.0, proposal.DecDegrees));

        await harness.WaitForSampleAsync(2);

        Assert.Null(harness.Recorder.Last<AlignmentWithheldEvent>());
        Assert.True(harness.Recorder.Last<SlewConfirmedEvent>()!.WasOverridden);
        Assert.Null(harness.Recorder.Last<SlewConfirmedEvent>()!.ReanchoredReason);

        (double ra, double dec) = harness.Mount.Slews[^1];
        Assert.Equal(sampleDeclination, SessionHarness.MechanicalDeclinationOf(ra, dec), tolerance: ToleranceDegrees);
    }

    /// <summary>
    /// Moving to a different declination is a different target, and the earlier
    /// samples are discarded.
    ///
    /// This is the case where being generous would be dangerous. Points at
    /// different declinations do not lie on one circle about the polar axis, so
    /// fitting them together does not merely add noise -- it produces a
    /// confident wrong answer, which is the single failure mode this project
    /// exists to avoid.
    /// </summary>
    [Fact]
    public async Task AnOverrideToADifferentDeclination_DiscardsWhatCameBeforeAndSaysWhy()
    {
        using var harness = new SessionHarness();
        await harness.ReadyAsync();
        await harness.WaitForSampleAsync(1);

        SlewProposedEvent proposal = await harness.Recorder.WaitForAsync<SlewProposedEvent>(e => e.PointIndex == 2);
        double before = harness.Recorder.Last<TargetSelectedEvent>()!.DeclinationDegrees;

        // A degree nearer the pole: unmistakably a different target.
        await harness.Session.SendAsync(new ConfirmSlewCommand(proposal.RaDegrees, proposal.DecDegrees + 1.0));

        AlignmentWithheldEvent withheld = await harness.Recorder.WaitForAsync<AlignmentWithheldEvent>();
        Assert.Contains("declination", withheld.Reason, StringComparison.OrdinalIgnoreCase);

        // Re-planned, and counting from one again.
        int restartedAt = harness.Recorder.IndexOfLast<AlignmentWithheldEvent>();
        await harness.Recorder.WaitForAsync<PointCapturedEvent>(e => e.Point.Index == 1, fromIndex: restartedAt, poll: harness.PollAsync);

        double after = harness.Recorder.Last<TargetSelectedEvent>()!.DeclinationDegrees;
        Assert.True(
            Math.Abs(after - before) > 0.5,
            $"the sequence should have re-anchored on the new declination, but went from {before:F3}° to {after:F3}°");
    }

    /// <summary>
    /// An estimate already on screen must not survive the re-anchor either: it
    /// was computed from samples that have just been thrown away.
    /// </summary>
    [Fact]
    public async Task ReanchoringAfterAnEstimate_WithdrawsThatEstimate()
    {
        using var harness = new SessionHarness();
        await harness.ReadyAsync();

        for (int point = 1; point <= 3; point++)
        {
            await harness.WaitForSampleAsync(point);
            await harness.Recorder.WaitForAsync<SlewProposedEvent>(e => e.PointIndex == point + 1);
            if (point < 3)
            {
                await harness.Session.SendAsync(new CaptureNextPointCommand());
            }
        }

        Assert.True(harness.Recorder.Last<AlignmentUpdatedEvent>() is not null, harness.Recorder.Trail());

        SlewProposedEvent proposal = harness.Recorder.Last<SlewProposedEvent>()!;
        await harness.Session.SendAsync(new ConfirmSlewCommand(proposal.RaDegrees, proposal.DecDegrees + 1.5));

        int withheldAt = harness.Recorder.IndexOfLast<AlignmentWithheldEvent>();
        int updatedAt = harness.Recorder.IndexOfLast<AlignmentUpdatedEvent>();

        Assert.True(withheldAt >= 0, harness.Recorder.Trail());
        Assert.True(
            withheldAt > updatedAt,
            "the withdrawal must come after the last estimate, or the UI would keep showing a number " +
            "computed from samples that no longer exist");
    }

    /// <summary>Coordinates that are not on the sky are refused before anything moves.</summary>
    [Theory]
    [InlineData(400.0, 30.0)]
    [InlineData(-10.0, 30.0)]
    [InlineData(120.0, 95.0)]
    [InlineData(120.0, -95.0)]
    [InlineData(double.NaN, 30.0)]
    public async Task CoordinatesOffTheSky_AreRefusedWithoutMoving(double ra, double dec)
    {
        using var harness = new SessionHarness(initialRotationDegrees: 12.0);
        await harness.ReadyAsync();

        await harness.Session.SendAsync(new ConfirmSlewCommand(ra, dec));

        Assert.True(harness.Recorder.Last<CommandRejectedEvent>() is not null, harness.Recorder.Trail());
        Assert.Empty(harness.Mount.Slews);
    }

    // ---- Preconditions ----

    /// <summary>
    /// A sequence needs a camera and a confirmed site, and says which is missing.
    /// Refusing is deliberately not a fault: nothing has broken, and presenting it
    /// as a fault would train the user to ignore faults.
    /// </summary>
    [Fact]
    public async Task WithoutACamera_StartingIsRefusedRatherThanFaulted()
    {
        using var harness = new SessionHarness();

        await harness.Session.SendAsync(new ConnectDeviceCommand(
            DeviceKind.Mount, AlignmentSession.AttachedProviderName, "mount"));
        await harness.ConfirmSiteAsync();
        await harness.StartAsync();

        CommandRejectedEvent? rejected = harness.Recorder.Last<CommandRejectedEvent>();
        Assert.True(rejected is not null, harness.Recorder.Trail());
        Assert.Contains("camera", rejected!.Reason, StringComparison.OrdinalIgnoreCase);
        Assert.Null(harness.Recorder.Last<SessionFaultedEvent>());
        Assert.Null(harness.Recorder.Last<SessionStartedEvent>());
    }

    /// <summary>
    /// And without a confirmed site, because the latitude enters the answer
    /// directly (D19) and is the one input the software cannot check for itself.
    /// </summary>
    [Fact]
    public async Task WithoutAConfirmedSite_StartingIsRefused()
    {
        using var harness = new SessionHarness();

        await harness.ConnectAsync();
        await harness.StartAsync();

        CommandRejectedEvent? rejected = harness.Recorder.Last<CommandRejectedEvent>();
        Assert.True(rejected is not null, harness.Recorder.Trail());
        Assert.Contains("site", rejected!.Reason, StringComparison.OrdinalIgnoreCase);
        Assert.Null(harness.Recorder.Last<SessionStartedEvent>());
    }

    /// <summary>
    /// Fewer than three samples cannot determine a circle (D7), so it is refused
    /// rather than attempted and withheld later.
    /// </summary>
    [Fact]
    public async Task FewerThanThreeCaptures_IsRefused()
    {
        using var harness = new SessionHarness();

        await harness.ConnectAsync();
        await harness.ConfirmSiteAsync();
        await harness.StartAsync(points: 2);

        Assert.True(harness.Recorder.Last<CommandRejectedEvent>() is not null, harness.Recorder.Trail());
        Assert.Null(harness.Recorder.Last<SessionStartedEvent>());
    }

    /// <summary>
    /// Devices cannot be swapped out from under a running sequence. Every sample
    /// already taken was made with the current pair, and pulling one mid-sequence
    /// would surface as an opaque driver error rather than as the deliberate act
    /// it was.
    /// </summary>
    [Fact]
    public async Task DisconnectingMidSequence_IsRefused()
    {
        using var harness = new SessionHarness();
        await harness.ReadyAsync();
        await harness.WaitForSampleAsync(1);

        await harness.Session.SendAsync(new DisconnectDeviceCommand(DeviceKind.Camera));

        Assert.True(harness.Recorder.Last<CommandRejectedEvent>() is not null, harness.Recorder.Trail());
        Assert.True(harness.Camera.IsConnected);

        // And the sequence is still usable.
        await harness.Session.SendAsync(new CaptureNextPointCommand());
        await harness.WaitForSampleAsync(2);
    }

    // ---- Status ----

    /// <summary>
    /// The mount's tracking state and reported position have to reach the UI, or
    /// the user cannot tell a stopped drive from a running one.
    /// </summary>
    [Fact]
    public async Task MountStatus_ReportsPositionAndTracking()
    {
        using var harness = new SessionHarness();
        await harness.ConnectAsync();

        await harness.PollAsync();

        MountStatusEvent? status = harness.Recorder.Last<MountStatusEvent>();
        Assert.True(status is not null, harness.Recorder.Trail());
        Assert.Equal(MountTrackingState.Tracking, status!.Tracking);
        Assert.InRange(status.RaDegrees, 0.0, 360.0);
        Assert.InRange(status.DecDegrees, -90.0, 90.0);
        Assert.False(status.IsMoving);
    }

    /// <summary>
    /// A driver that does not report tracking must produce "unknown", never
    /// "stopped" -- and one that does report it stopped must say so.
    /// </summary>
    [Fact]
    public async Task AMountWithTheDriveStopped_SaysSoRatherThanReportingNothing()
    {
        using var harness = new SessionHarness(tracking: false);
        await harness.ConnectAsync();
        await harness.PollAsync();

        Assert.Equal(MountTrackingState.Stopped, harness.Recorder.Last<MountStatusEvent>()!.Tracking);
    }

    // ---- The frame ----

    /// <summary>
    /// The frame is announced before the solve is attempted. A user whose solve
    /// has just failed needs the image, because "no stars detected" is answered
    /// by looking at it.
    /// </summary>
    [Fact]
    public async Task TheFrameIsAnnouncedBeforeItIsSolved()
    {
        using var harness = new SessionHarness();
        await harness.ReadyAsync();
        await harness.WaitForSampleAsync(1);

        SolveStartedEvent started = harness.Recorder.Last<SolveStartedEvent>()!;
        IReadOnlyList<EngineEvent> events = harness.Recorder.Events;
        int frameAt = events.ToList().FindIndex(e => e is FrameCapturedEvent f && f.FitsPath == started.FitsPath);
        int solveAt = events.ToList().IndexOf(started);
        int sampleAt = harness.Recorder.IndexOfLast<PointCapturedEvent>();

        Assert.True(frameAt >= 0 && frameAt < solveAt && solveAt < sampleAt, harness.Recorder.Trail());
    }

    /// <summary>
    /// The session tells the camera what only the session knows, so the frame
    /// can record it: where the mount believes it is pointing (D20).
    /// </summary>
    [Fact]
    public async Task TheCameraIsToldWhereTheMountThinksItIsPointing()
    {
        using var harness = new SessionHarness();
        await harness.ConnectAsync();
        await harness.PollAsync();

        int framesBefore = harness.Camera.Exposures;
        await harness.Recorder.WaitForAsync<FrameCapturedEvent>(fromIndex: harness.Recorder.Count);
        await harness.Recorder.WaitForAsync<FrameCapturedEvent>(fromIndex: harness.Recorder.Count);

        MountStatusEvent status = harness.Recorder.Last<MountStatusEvent>()!;
        CaptureContext? context = harness.Camera.Frames.Skip(framesBefore).Last().Context;

        Assert.NotNull(context);
        Assert.Equal(status.RaDegrees, context!.MountRaDegrees!.Value, precision: 9);
        Assert.Equal(status.DecDegrees, context.MountDecDegrees!.Value, precision: 9);
    }

    /// <summary>
    /// A failed solve is reported and its frame kept for diagnosis (D26), rather
    /// than faulting the sequence or deleting the one frame worth looking at.
    /// </summary>
    [Fact]
    public async Task AFailedSolve_IsReportedAndItsFrameKept()
    {
        using var harness = new SessionHarness(solver: new HopelessSolver());
        await harness.ReadyAsync();

        SolveFailedEvent failed = await harness.Recorder.WaitForAsync<SolveFailedEvent>();

        Assert.Null(harness.Recorder.Last<SessionFaultedEvent>());
        Assert.Null(harness.Recorder.Last<PointCapturedEvent>());
        Assert.Equal(1, failed.ConsecutiveFailures);
    }

    // ---- Readout modes ----

    /// <summary>
    /// A camera that offers no readout modes refuses a selection rather than
    /// pretending to have made one. A silently ignored setting is the worst
    /// outcome: the user believes they are reading out at sixteen bits and has
    /// no way to find out otherwise.
    /// </summary>
    [Fact]
    public async Task ACameraWithNoReadoutModes_RefusesASelection()
    {
        using var harness = new SessionHarness();
        await harness.ConnectAsync();

        await harness.Session.SendAsync(new SetReadoutModeCommand(1));

        Assert.True(harness.Recorder.Last<CommandRejectedEvent>() is not null, harness.Recorder.Trail());
        Assert.Null(harness.Recorder.Last<ReadoutModeChangedEvent>());
    }

    /// <summary>
    /// The mode can be changed part-way through a sequence (D22, D25 as revised).
    /// It was refused, on the reasoning that the fit weights every frame alike;
    /// but the bit depth changes how noisy a solved position is, not where it
    /// is, and refusing it left a user stuck with a mode found wrong mid-sequence.
    /// </summary>
    [Fact]
    public async Task ChangingTheReadoutModeMidSequence_IsAccepted()
    {
        var mountOptions = new SimulatedMountOptions(
            Site, new MountMisalignment(30.0, -25.0), InitialMechanicalRotationDegrees: -70.0);
        var provider = new SimulatedDeviceProvider(
            StarCatalog.LoadCsv(CatalogPath), mountOptions,
            new SimulatedCameraOptions(WidthPixels: 320, HeightPixels: 240));
        var mount = new RecordingMount(provider.MountInstance);

        using ICamera camera = provider.OpenCamera("sim-camera");
        using var session = new AlignmentSession(camera, mount, new PerfectSolver(provider.MountInstance, Site),
            new AlignmentSessionOptions(ExposureDuration: TimeSpan.FromMilliseconds(20), FailedSolvesDirectory: TempDirectory()));
        var recorder = new Recorder();
        using IDisposable subscription = session.Events.Subscribe(recorder);

        await session.SendAsync(new ConnectDeviceCommand(DeviceKind.Camera, AlignmentSession.AttachedProviderName, "camera"));
        await session.SendAsync(new ConnectDeviceCommand(DeviceKind.Mount, AlignmentSession.AttachedProviderName, "mount"));
        await session.SendAsync(new ConfigureSiteCommand(Site.LatitudeDegrees, Site.LongitudeDegrees, Site.HeightMeters));
        await session.SendAsync(new StartSessionCommand(new SessionConfiguration(5, 60.0)));
        Assert.NotNull(recorder.Last<SessionStartedEvent>());

        await session.SendAsync(new SetReadoutModeCommand(1));

        Assert.Null(recorder.Last<CommandRejectedEvent>());
        Assert.Equal(8, recorder.Last<ReadoutModeChangedEvent>()!.BitDepth);
    }

    private static string CatalogPath =>
        Path.Combine(AppContext.BaseDirectory, "fixtures", "tycho2_subset.csv");

    private static string TempDirectory() => Path.Combine(Path.GetTempPath(), $"fpa-failed-{Guid.NewGuid():N}");

    /// <summary>
    /// The camera reports its readout modes on connect, so the UI can offer them
    /// without a second round trip.
    /// </summary>
    [Fact]
    public async Task ConnectingReportsTheAvailableReadoutModes()
    {
        var options = new SimulatedMountOptions(Site, new MountMisalignment(10.0, 10.0));
        var provider = new SimulatedDeviceProvider(StarCatalog.LoadCsv(CatalogPath), options,
            new SimulatedCameraOptions(WidthPixels: 320, HeightPixels: 240));

        using ICamera camera = provider.OpenCamera("sim-camera");
        using IMount mount = provider.OpenMount("sim-mount");
        using var session = new AlignmentSession(camera, mount, new PerfectSolver(provider.MountInstance, Site),
            new AlignmentSessionOptions(FailedSolvesDirectory: TempDirectory()));

        var recorder = new Recorder();
        using IDisposable subscription = session.Events.Subscribe(recorder);

        await session.SendAsync(new ConnectDeviceCommand(
            DeviceKind.Camera, AlignmentSession.AttachedProviderName, "camera"));

        CameraDescription? description = recorder.Last<DeviceConnectedEvent>()!.Camera;
        Assert.NotNull(description);
        Assert.Equal(2, description!.ReadoutModes!.Count);
        Assert.Contains(description.ReadoutModes, m => m.BitDepth == 16);
        Assert.Contains(description.ReadoutModes, m => m.BitDepth == 8);

        // The label carries the depth, since that is the part of the choice that
        // has consequences.
        Assert.All(description.ReadoutModes, m => Assert.Contains("-bit", m.Label, StringComparison.Ordinal));
    }

    // ---- Site disagreement ----

    /// <summary>
    /// When the mount's own site differs from the confirmed one, the confirmed
    /// one is used and the difference is reported (D19).
    /// </summary>
    [Fact]
    public async Task AMountReportingADifferentSite_IsNotedButNotObeyed()
    {
        using var harness = new SessionHarness();
        await harness.ConnectAsync();

        // Half a degree of latitude away -- thirty arcminutes of pure bias in
        // the reported altitude error if it were used by mistake.
        await harness.Session.SendAsync(new ConfigureSiteCommand(SessionHarness.Latitude + 0.5, 15.0, 200.0));

        SiteConfiguredEvent? configured = harness.Recorder.Last<SiteConfiguredEvent>();
        Assert.True(configured is not null, harness.Recorder.Trail());
        Assert.Equal(SessionHarness.Latitude + 0.5, configured!.LatitudeDegrees, precision: 6);
        Assert.NotNull(configured.MountReportedDisagreement);
        Assert.Contains("latitude", configured.MountReportedDisagreement!, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>An agreeing mount produces no note, so that the note means something when it appears.</summary>
    [Fact]
    public async Task AMountReportingTheSameSite_ProducesNoNote()
    {
        using var harness = new SessionHarness();
        await harness.ConnectAsync();
        await harness.ConfirmSiteAsync();

        Assert.Null(harness.Recorder.Last<SiteConfiguredEvent>()!.MountReportedDisagreement);
    }

    // ---- A mount that cannot slew ----

    /// <summary>
    /// A connected mount that cannot slew is read but never driven (D10 as
    /// revised): the engine says what to do instead, and a slew command is
    /// refused rather than attempted.
    /// </summary>
    [Fact]
    public async Task AMountThatCannotSlew_IsNeverCommanded()
    {
        using var harness = new SessionHarness(canSlew: false);
        await harness.ReadyAsync();

        Assert.Equal(SequenceMode.Observed, harness.Recorder.Last<SessionStartedEvent>()!.Mode);
        Assert.False(harness.Recorder.Last<SlewProposedEvent>()!.RequiresMotion);
        Assert.NotNull(harness.Recorder.Last<ManualActionRequiredEvent>());

        await harness.WaitForSampleAsync(1);
        await harness.Session.SendAsync(new CaptureNextPointCommand());

        Assert.True(harness.Recorder.Last<CommandRejectedEvent>() is not null, harness.Recorder.Trail());
        Assert.Empty(harness.Mount.Slews);
    }
}
