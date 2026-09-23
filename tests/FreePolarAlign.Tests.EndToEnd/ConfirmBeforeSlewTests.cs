using FreePolarAlign.Core.Alignment;
using FreePolarAlign.Core.Astrometry;
using FreePolarAlign.Core.Engine;
using FreePolarAlign.Devices;
using FreePolarAlign.Devices.Simulated;
using FreePolarAlign.Devices.Simulated.SyntheticSky;
using FreePolarAlign.Session;
using FreePolarAlign.Solving;
using Xunit;

namespace FreePolarAlign.Tests.EndToEnd;

/// <summary>
/// D18: the mount stands still until it is told to move, and an override that
/// changes declination restarts the sequence rather than corrupting it.
///
/// These tests run against the real mount mechanics -- the same
/// <see cref="SimulatedMount"/> the convergence tests use, with its injected
/// misalignment and cone error -- but with a noiseless solver that simply
/// reports where the telescope is actually pointing. That substitution buys two
/// things worth having: the tests need no quad database, so they run everywhere
/// and in milliseconds, and any discrepancy in the commanded coordinates is
/// unambiguous rather than lost in solve noise.
///
/// What they do *not* test is accuracy. That is the convergence tests' job, with
/// a real solver on rendered frames.
/// </summary>
public class ConfirmBeforeSlewTests
{
    private const double Latitude = 45.0;
    private static readonly GeodeticLocation Site = new(Latitude, 15.0, 200.0);
    private static readonly ObserverSite Observer = new(Latitude, 15.0, 200.0);

    /// <summary>A tenth of an arcminute -- far below anything that matters here, and far above float noise.</summary>
    private const double ToleranceDegrees = 0.1 / 60.0;

    // ---- Fakes ----

    /// <summary>
    /// Wraps the real simulated mount and records every slew it is asked to
    /// perform. The recording is the point: "nothing moved" is only a
    /// meaningful assertion if movement is observable.
    /// </summary>
    private sealed class RecordingMount : IMount
    {
        private readonly SimulatedMount _inner;

        public RecordingMount(SimulatedMount inner) => _inner = inner;

        public List<(double Ra, double Dec)> Slews { get; } = new();

        public SimulatedMount Inner => _inner;

        public string Name => _inner.Name;

        public bool IsConnected => _inner.IsConnected;

        public bool CanSlewAsync => _inner.CanSlewAsync;

        public Task ConnectAsync(CancellationToken cancellationToken = default) => _inner.ConnectAsync(cancellationToken);

        public Task DisconnectAsync(CancellationToken cancellationToken = default) => _inner.DisconnectAsync(cancellationToken);

        public Task<MountPosition> GetPositionAsync(CancellationToken cancellationToken = default) =>
            _inner.GetPositionAsync(cancellationToken);

        public async Task SlewToCoordinatesAsync(double raDegrees, double decDegrees, CancellationToken cancellationToken = default)
        {
            Slews.Add((raDegrees, decDegrees));
            await _inner.SlewToCoordinatesAsync(raDegrees, decDegrees, cancellationToken).ConfigureAwait(false);
        }

        public Task<PierSide> GetSideOfPierAsync(CancellationToken cancellationToken = default) =>
            _inner.GetSideOfPierAsync(cancellationToken);

        public Task<GeodeticLocation> GetSiteLocationAsync(CancellationToken cancellationToken = default) =>
            _inner.GetSiteLocationAsync(cancellationToken);

        public void Dispose() => _inner.Dispose();
    }

    /// <summary>A camera that produces nothing. The solver here never reads the file.</summary>
    private sealed class StubCamera : ICamera
    {
        public string Name => "Stub Camera";

        public int SensorWidthPixels => 4000;

        public int SensorHeightPixels => 3000;

        public double PixelSizeMicrons => 3.76;

        public bool IsConnected { get; private set; }

        public int Exposures { get; private set; }

        /// <summary>No modes, which is the common case for a real camera and the one worth exercising by default.</summary>
        public IReadOnlyList<CameraReadoutMode> ReadoutModes => Array.Empty<CameraReadoutMode>();

        public int? ReadoutModeIndex => null;

        public Task SetReadoutModeAsync(int index, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException("This stub camera has no selectable readout modes.");

        public Task ConnectAsync(CancellationToken cancellationToken = default)
        {
            IsConnected = true;
            return Task.CompletedTask;
        }

        public Task DisconnectAsync(CancellationToken cancellationToken = default)
        {
            IsConnected = false;
            return Task.CompletedTask;
        }

        /// <summary>What the session handed down with the most recent exposure.</summary>
        public CaptureContext? LastContext { get; private set; }

        public Task<CapturedImage> ExposeAsync(
            TimeSpan duration, CaptureContext? context = null, CancellationToken cancellationToken = default)
        {
            if (!IsConnected)
            {
                throw new InvalidOperationException("The stub camera is not connected.");
            }

            Exposures++;
            LastContext = context;
            return Task.FromResult(new CapturedImage("stub.fits", DateTime.UtcNow, duration));
        }

        public void Dispose() => IsConnected = false;
    }

    /// <summary>
    /// Reports exactly where the telescope is pointing, converted back to sky
    /// coordinates through the same atmosphere the session will use to convert
    /// them forward again. The round trip is therefore exact, which makes any
    /// difference in a commanded coordinate attributable to the engine.
    /// </summary>
    private sealed class PerfectSolver : ISolver
    {
        private readonly SimulatedMount _mount;

        public PerfectSolver(SimulatedMount mount) => _mount = mount;

        public string Name => "Perfect (test)";

        public Task<PlateSolveResult> SolveAsync(PlateSolveRequest request, CancellationToken cancellationToken = default)
        {
            DateTime now = DateTime.UtcNow;
            HorizontalCoordinates pointing = _mount.PhysicalPointingAt(now);

            (double ra, double dec) = TopocentricConverter.FromAltAz(
                pointing, now, Observer, AtmosphericConditions.Standard);

            return Task.FromResult(PlateSolveResult.Succeeded(new PlateSolveSolution(
                ra, dec,
                PixelScaleArcsecPerPixel: 2.0,
                RotationDegrees: 0.0,
                Cd1_1: -2.0 / 3600.0, Cd1_2: 0.0, Cd2_1: 0.0, Cd2_2: 2.0 / 3600.0,
                MatchedStarCount: 40,
                SolveDuration: TimeSpan.Zero)));
        }
    }

    private sealed class Recorder : IObserver<EngineEvent>
    {
        public List<EngineEvent> Events { get; } = new();

        public void OnNext(EngineEvent value) => Events.Add(value);

        public void OnError(Exception error) { }

        public void OnCompleted() { }

        public T? Last<T>() where T : EngineEvent => Events.OfType<T>().LastOrDefault();

        public IEnumerable<T> All<T>() where T : EngineEvent => Events.OfType<T>();

        public string Trail() => Environment.NewLine + string.Join(
            Environment.NewLine,
            Events.Select(e => EngineEventNarrator.Describe(e))
                .Where(n => !string.IsNullOrEmpty(n.Message))
                .Select(n => $"  {n.Severity}: {n.Message}"));
    }

    // ---- Harness ----

    private sealed class Harness : IDisposable
    {
        public Harness(bool manualMode = false, double coneErrorArcminutes = 20.0)
        {
            var options = new SimulatedMountOptions(
                Site,
                new MountMisalignment(30.0, -25.0),
                ConeErrorArcminutes: coneErrorArcminutes,
                ConePhaseDegrees: 35.0,
                Tracking: true);

            Simulated = new SimulatedMount(options);
            Mount = new RecordingMount(Simulated);
            Camera = new StubCamera();
            Solver = new PerfectSolver(Simulated);

            Session = new AlignmentSession(Camera, Mount, Solver, new AlignmentSessionOptions(
                CaptureCount: 5, SweepDegrees: 60.0, ManualMode: manualMode, ExpectedSolveNoiseArcseconds: 3.0));

            Recorder = new Recorder();
            _subscription = Session.Events.Subscribe(Recorder);
        }

        private readonly IDisposable _subscription;

        public SimulatedMount Simulated { get; }

        public RecordingMount Mount { get; }

        public StubCamera Camera { get; }

        public PerfectSolver Solver { get; }

        public AlignmentSession Session { get; }

        public Recorder Recorder { get; }

        public async Task ConnectAsync()
        {
            await Session.SendAsync(new ConnectDeviceCommand(
                DeviceKind.Camera, AlignmentSession.AttachedProviderName, "camera"));
            await Session.SendAsync(new ConnectDeviceCommand(
                DeviceKind.Mount, AlignmentSession.AttachedProviderName, "mount"));
        }

        public Task ConfirmSiteAsync() => Session
            .SendAsync(new ConfigureSiteCommand(Site.LatitudeDegrees, Site.LongitudeDegrees, Site.HeightMeters))
            .AsTask();

        public Task StartAsync(int points = 5, double sweep = 60.0) => Session
            .SendAsync(new StartSessionCommand(new SessionConfiguration(points, sweep, TimeSpan.FromSeconds(1))))
            .AsTask();

        public async Task ReadyAsync()
        {
            await ConnectAsync();
            await ConfirmSiteAsync();
            await StartAsync();
        }

        public void Dispose()
        {
            _subscription.Dispose();
            Session.Dispose();
        }
    }

    /// <summary>
    /// The mechanical declination a commanded sky position corresponds to --
    /// computed the same geometric way the engine does, since it has to invert
    /// what the mount will do rather than model the atmosphere better than the
    /// mount does.
    /// </summary>
    private static double MechanicalDeclinationOf(double raDegrees, double decDegrees)
    {
        HorizontalCoordinates pointing = TopocentricConverter.ToAltAz(
            raDegrees, decDegrees, DateTime.UtcNow, Observer, AtmosphericConditions.Vacuum);

        var (_, declination) = MountMechanics.Decompose(Latitude, pointing);
        return declination;
    }

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
        using var harness = new Harness();

        await harness.ConnectAsync();
        await harness.ConfirmSiteAsync();
        await harness.StartAsync();

        Assert.Empty(harness.Mount.Slews);
        Assert.Equal(0, harness.Camera.Exposures);

        SlewProposedEvent? proposed = harness.Recorder.Last<SlewProposedEvent>();
        Assert.True(proposed is not null, harness.Recorder.Trail());
        Assert.Equal(1, proposed!.PointIndex);
    }

    /// <summary>
    /// And the first point is taken where the telescope already points, so the
    /// scale is measured before anything is asked to move -- which is the one
    /// frame most worth having before a slew, since it is what makes every later
    /// solve a bounded search instead of a blind one.
    /// </summary>
    [Fact]
    public async Task TheFirstPoint_NeedsNoMovementAtAll()
    {
        using var harness = new Harness();
        await harness.ReadyAsync();

        Assert.False(harness.Recorder.Last<SlewProposedEvent>()!.RequiresMotion);

        await harness.Session.SendAsync(new CaptureNextPointCommand());

        Assert.Empty(harness.Mount.Slews);
        Assert.Equal(1, harness.Camera.Exposures);
        Assert.Equal(1, harness.Recorder.Last<PointCapturedEvent>()!.Point.Index);
    }

    /// <summary>
    /// Every later point is proposed and then waited on. The engine does not
    /// chain one capture into the next slew, however obvious the next step is.
    /// </summary>
    [Fact]
    public async Task EveryLaterPoint_WaitsToBeConfirmed()
    {
        using var harness = new Harness();
        await harness.ReadyAsync();

        await harness.Session.SendAsync(new CaptureNextPointCommand());

        // A proposal for point 2 exists, and it does require movement -- but
        // nothing has moved.
        SlewProposedEvent? proposed = harness.Recorder.Last<SlewProposedEvent>();
        Assert.Equal(2, proposed!.PointIndex);
        Assert.True(proposed.RequiresMotion);
        Assert.Empty(harness.Mount.Slews);

        await harness.Session.SendAsync(new CaptureNextPointCommand());

        Assert.Single(harness.Mount.Slews);
        Assert.Equal(2, harness.Recorder.Last<PointCapturedEvent>()!.Point.Index);
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
    public async Task ConfirmingTheProposal_HoldsTheMechanicalDeclinationOnEveryPoint()
    {
        using var harness = new Harness();
        await harness.ReadyAsync();

        double planned = harness.Recorder.Last<TargetSelectedEvent>()!.DeclinationDegrees;

        for (int i = 0; i < 5; i++)
        {
            await harness.Session.SendAsync(new CaptureNextPointCommand());
            Assert.True(harness.Recorder.Last<SessionFaultedEvent>() is null, harness.Recorder.Trail());
        }

        Assert.Equal(4, harness.Mount.Slews.Count);

        foreach ((double ra, double dec) in harness.Mount.Slews)
        {
            Assert.Equal(planned, MechanicalDeclinationOf(ra, dec), tolerance: ToleranceDegrees);
        }
    }

    // ---- Overriding ----

    /// <summary>
    /// Moving along the same arc is an ordinary override: the user can see a tree
    /// where the engine cannot, and a planner that insisted on its own hour angle
    /// would be wrong more often than they are. The sequence continues, and the
    /// declination is snapped back to the plan's exactly -- because the few
    /// arcminutes a hand-typed declination would otherwise sit out by is real
    /// declination movement, and the sequence must not contain any.
    /// </summary>
    [Fact]
    public async Task AnOverrideAlongTheSameArc_KeepsTheSequenceAndSnapsTheDeclination()
    {
        using var harness = new Harness();
        await harness.ReadyAsync();

        double planned = harness.Recorder.Last<TargetSelectedEvent>()!.DeclinationDegrees;

        await harness.Session.SendAsync(new CaptureNextPointCommand());
        SlewProposedEvent proposal = harness.Recorder.Last<SlewProposedEvent>()!;

        // Ten degrees further west along the same declination.
        await harness.Session.SendAsync(new ConfirmSlewCommand(
            (proposal.RaDegrees + 10.0) % 360.0, proposal.DecDegrees));

        Assert.True(harness.Recorder.Last<AlignmentWithheldEvent>() is null, harness.Recorder.Trail());
        Assert.True(harness.Recorder.Last<SlewConfirmedEvent>()!.WasOverridden);
        Assert.Null(harness.Recorder.Last<SlewConfirmedEvent>()!.ReanchoredReason);

        // The sequence carried on rather than restarting.
        Assert.Equal(2, harness.Recorder.Last<PointCapturedEvent>()!.Point.Index);

        (double ra, double dec) = harness.Mount.Slews[^1];
        Assert.Equal(planned, MechanicalDeclinationOf(ra, dec), tolerance: ToleranceDegrees);
    }

    /// <summary>
    /// Moving to a different declination is a different target, and the earlier
    /// captures are discarded.
    ///
    /// This is the case where being generous would be dangerous. Points at
    /// different declinations do not lie on one circle about the polar axis, so
    /// fitting them together does not merely add noise -- it produces a
    /// confident wrong answer, which is the single failure mode this project
    /// exists to avoid. Restarting costs the user twenty minutes; not restarting
    /// costs them a night of trailed subframes and no idea why.
    /// </summary>
    [Fact]
    public async Task AnOverrideToADifferentDeclination_DiscardsWhatCameBeforeAndSaysWhy()
    {
        using var harness = new Harness();
        await harness.ReadyAsync();

        await harness.Session.SendAsync(new CaptureNextPointCommand());
        await harness.Session.SendAsync(new CaptureNextPointCommand());
        Assert.Equal(2, harness.Recorder.Last<PointCapturedEvent>()!.Point.Index);

        SlewProposedEvent proposal = harness.Recorder.Last<SlewProposedEvent>()!;
        double before = harness.Recorder.Last<TargetSelectedEvent>()!.DeclinationDegrees;

        // A degree further south: unmistakably a different target rather than a
        // different hour angle on the same one.
        await harness.Session.SendAsync(new ConfirmSlewCommand(
            proposal.RaDegrees, proposal.DecDegrees - 1.0));

        AlignmentWithheldEvent? withheld = harness.Recorder.Last<AlignmentWithheldEvent>();
        Assert.True(withheld is not null, harness.Recorder.Trail());
        Assert.Contains("declination", withheld!.Reason, StringComparison.OrdinalIgnoreCase);

        // Re-planned, and counting from one again.
        Assert.Equal(1, harness.Recorder.Last<PointCapturedEvent>()!.Point.Index);
        Assert.True(harness.Recorder.All<TargetSelectedEvent>().Count() >= 2, harness.Recorder.Trail());

        double after = harness.Recorder.Last<TargetSelectedEvent>()!.DeclinationDegrees;
        Assert.True(
            Math.Abs(after - before) > 0.5,
            $"the sequence should have re-anchored on the new declination, but went from {before:F3}° to {after:F3}°");
    }

    /// <summary>
    /// An estimate already on screen must not survive the re-anchor either: it
    /// was computed from captures that have just been thrown away.
    /// </summary>
    [Fact]
    public async Task ReanchoringAfterAnEstimate_WithdrawsThatEstimate()
    {
        using var harness = new Harness();
        await harness.ReadyAsync();

        for (int i = 0; i < 3; i++)
        {
            await harness.Session.SendAsync(new CaptureNextPointCommand());
        }

        // Three points is the minimum the fit needs (D7), so there is now an
        // estimate to withdraw.
        Assert.True(harness.Recorder.Last<AlignmentUpdatedEvent>() is not null, harness.Recorder.Trail());

        SlewProposedEvent proposal = harness.Recorder.Last<SlewProposedEvent>()!;
        await harness.Session.SendAsync(new ConfirmSlewCommand(
            proposal.RaDegrees, proposal.DecDegrees - 1.5));

        int withheldAt = harness.Recorder.Events.FindLastIndex(e => e is AlignmentWithheldEvent);
        int updatedAt = harness.Recorder.Events.FindLastIndex(e => e is AlignmentUpdatedEvent);

        Assert.True(withheldAt >= 0, harness.Recorder.Trail());
        Assert.True(
            withheldAt > updatedAt,
            "the withdrawal must come after the last estimate, or the UI would keep showing a number " +
            "computed from captures that no longer exist");
    }

    /// <summary>
    /// Coordinates that are not on the sky are refused before anything moves.
    /// </summary>
    [Theory]
    [InlineData(400.0, 30.0)]
    [InlineData(-10.0, 30.0)]
    [InlineData(120.0, 95.0)]
    [InlineData(120.0, -95.0)]
    [InlineData(double.NaN, 30.0)]
    public async Task CoordinatesOffTheSky_AreRefusedWithoutMoving(double ra, double dec)
    {
        using var harness = new Harness();
        await harness.ReadyAsync();
        await harness.Session.SendAsync(new CaptureNextPointCommand());

        int slewsBefore = harness.Mount.Slews.Count;
        await harness.Session.SendAsync(new ConfirmSlewCommand(ra, dec));

        Assert.True(harness.Recorder.Last<CommandRejectedEvent>() is not null, harness.Recorder.Trail());
        Assert.Equal(slewsBefore, harness.Mount.Slews.Count);
    }

    // ---- Preconditions ----

    /// <summary>
    /// A sequence needs a camera, a mount and a confirmed site, and says which is
    /// missing. Refusing is deliberately not a fault: nothing has broken, and
    /// presenting it as a fault would train the user to ignore faults.
    /// </summary>
    [Fact]
    public async Task WithoutACamera_StartingIsRefusedRatherThanFaulted()
    {
        using var harness = new Harness();

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
        using var harness = new Harness();

        await harness.ConnectAsync();
        await harness.StartAsync();

        CommandRejectedEvent? rejected = harness.Recorder.Last<CommandRejectedEvent>();
        Assert.True(rejected is not null, harness.Recorder.Trail());
        Assert.Contains("site", rejected!.Reason, StringComparison.OrdinalIgnoreCase);
        Assert.Null(harness.Recorder.Last<SessionStartedEvent>());
    }

    /// <summary>
    /// Fewer than three captures cannot determine a circle (D7), so it is
    /// refused rather than attempted and withheld later.
    /// </summary>
    [Fact]
    public async Task FewerThanThreeCaptures_IsRefused()
    {
        using var harness = new Harness();

        await harness.ConnectAsync();
        await harness.ConfirmSiteAsync();
        await harness.StartAsync(points: 2);

        Assert.True(harness.Recorder.Last<CommandRejectedEvent>() is not null, harness.Recorder.Trail());
        Assert.Null(harness.Recorder.Last<SessionStartedEvent>());
    }

    /// <summary>
    /// Devices cannot be swapped out from under a running sequence. Every capture
    /// already taken was made with the current pair, and pulling one mid-sequence
    /// would surface as an opaque driver error rather than as the deliberate act
    /// it was.
    /// </summary>
    [Fact]
    public async Task DisconnectingMidSequence_IsRefused()
    {
        using var harness = new Harness();
        await harness.ReadyAsync();
        await harness.Session.SendAsync(new CaptureNextPointCommand());

        await harness.Session.SendAsync(new DisconnectDeviceCommand(DeviceKind.Camera));

        Assert.True(harness.Recorder.Last<CommandRejectedEvent>() is not null, harness.Recorder.Trail());
        Assert.True(harness.Camera.IsConnected);

        // And the sequence is still usable.
        await harness.Session.SendAsync(new CaptureNextPointCommand());
        Assert.Equal(2, harness.Recorder.Last<PointCapturedEvent>()!.Point.Index);
    }

    // ---- Status ----

    /// <summary>
    /// The mount's tracking state and reported position have to reach the UI, or
    /// the user cannot tell a stopped drive from a running one -- and a stopped
    /// drive during a sequence means every frame after the first is of somewhere
    /// else.
    /// </summary>
    [Fact]
    public async Task MountStatus_ReportsPositionAndTracking()
    {
        using var harness = new Harness();
        await harness.ConnectAsync();

        await harness.Session.SendAsync(new RefreshMountStatusCommand());

        MountStatusEvent? status = harness.Recorder.Last<MountStatusEvent>();
        Assert.True(status is not null, harness.Recorder.Trail());
        Assert.Equal(MountTrackingState.Tracking, status!.Tracking);
        Assert.InRange(status.RaDegrees, 0.0, 360.0);
        Assert.InRange(status.DecDegrees, -90.0, 90.0);
    }

    /// <summary>
    /// A driver that does not report tracking must produce "unknown", never
    /// "stopped". They are indistinguishable in a boolean and mean very
    /// different things to someone deciding whether the numbers they are
    /// watching should be changing on their own.
    /// </summary>
    [Fact]
    public async Task AMountWithTheDriveStopped_SaysSoRatherThanReportingNothing()
    {
        var options = new SimulatedMountOptions(Site, new MountMisalignment(10.0, 10.0), Tracking: false);
        var simulated = new SimulatedMount(options);
        var mount = new RecordingMount(simulated);
        var camera = new StubCamera();

        using var session = new AlignmentSession(camera, mount, new PerfectSolver(simulated));
        var recorder = new Recorder();
        using IDisposable subscription = session.Events.Subscribe(recorder);

        await session.SendAsync(new ConnectDeviceCommand(
            DeviceKind.Mount, AlignmentSession.AttachedProviderName, "mount"));
        await session.SendAsync(new RefreshMountStatusCommand());

        Assert.Equal(MountTrackingState.Stopped, recorder.Last<MountStatusEvent>()!.Tracking);
    }

    // ---- The captured frame ----

    /// <summary>
    /// The frame is announced before the solve is attempted. A user whose solve
    /// has just failed needs the image, because "no stars detected" is answered
    /// by looking at it -- a lens cap, cloud, wild defocus and a tracking runaway
    /// are all obvious in the picture and none are distinguishable from the
    /// message.
    /// </summary>
    [Fact]
    public async Task TheFrameIsAnnouncedBeforeTheSolveIsAttempted()
    {
        using var harness = new Harness();
        await harness.ReadyAsync();

        await harness.Session.SendAsync(new CaptureNextPointCommand());

        int frameAt = harness.Recorder.Events.FindIndex(e => e is FrameCapturedEvent);
        int solvedAt = harness.Recorder.Events.FindIndex(e => e is PointCapturedEvent);

        Assert.True(frameAt >= 0, harness.Recorder.Trail());
        Assert.True(frameAt < solvedAt, "the frame must be available before the solve resolves it");
        Assert.Equal(1, harness.Recorder.Last<FrameCapturedEvent>()!.PointIndex);
    }

    /// <summary>
    /// The session tells the camera what only the session knows, so the frame
    /// can record it.
    ///
    /// Where the mount believes it is pointing and what focal length is in use
    /// are not the camera's to discover, and a frame found in a temp directory
    /// afterwards is a rectangle of numbers without them. The position asserted
    /// here is the one the session had just read from the mount and published,
    /// so the frame and the event agree about the same instant.
    /// </summary>
    [Fact]
    public async Task TheCameraIsToldWhereTheMountThinksItIsPointing()
    {
        using var harness = new Harness();
        await harness.ReadyAsync();

        await harness.Session.SendAsync(new CaptureNextPointCommand());

        CaptureContext? context = harness.Camera.LastContext;
        Assert.NotNull(context);

        MountStatusEvent status = harness.Recorder.Last<MountStatusEvent>()!;
        Assert.Equal(status.RaDegrees, context!.MountRaDegrees!.Value, precision: 9);
        Assert.Equal(status.DecDegrees, context.MountDecDegrees!.Value, precision: 9);
    }

    /// <summary>
    /// And it is announced even when the solve fails, which is the case it
    /// exists for.
    /// </summary>
    [Fact]
    public async Task AFailedSolve_StillLeavesTheFrameAvailable()
    {
        var options = new SimulatedMountOptions(Site, new MountMisalignment(30.0, -25.0));
        var simulated = new SimulatedMount(options);
        var mount = new RecordingMount(simulated);
        var camera = new StubCamera();

        using var session = new AlignmentSession(camera, mount, new HopelessSolver());
        var recorder = new Recorder();
        using IDisposable subscription = session.Events.Subscribe(recorder);

        await session.SendAsync(new ConnectDeviceCommand(
            DeviceKind.Camera, AlignmentSession.AttachedProviderName, "camera"));
        await session.SendAsync(new ConnectDeviceCommand(
            DeviceKind.Mount, AlignmentSession.AttachedProviderName, "mount"));
        await session.SendAsync(new ConfigureSiteCommand(
            Site.LatitudeDegrees, Site.LongitudeDegrees, Site.HeightMeters));
        await session.SendAsync(new StartSessionCommand(new SessionConfiguration(5, 60.0, TimeSpan.FromSeconds(1))));

        await session.SendAsync(new CaptureNextPointCommand());

        Assert.NotNull(recorder.Last<FrameCapturedEvent>());
        Assert.NotNull(recorder.Last<CaptureFailedEvent>());
        Assert.Null(recorder.Last<PointCapturedEvent>());
    }

    /// <summary>A solver that never matches anything, for the failure path.</summary>
    private sealed class HopelessSolver : ISolver
    {
        public string Name => "Hopeless (test)";

        public Task<PlateSolveResult> SolveAsync(PlateSolveRequest request, CancellationToken cancellationToken = default) =>
            Task.FromResult(PlateSolveResult.Failed(
                PlateSolveFailureReason.NoStarsDetected, "No stars were detected in the image."));
    }

    // ---- Readout modes ----

    /// <summary>
    /// A camera that offers no readout modes refuses a selection rather than
    /// pretending to have made one. Most cameras are in this position, and a
    /// silently ignored setting is the worst outcome: the user believes they are
    /// reading out at sixteen bits and has no way to find out otherwise.
    /// </summary>
    [Fact]
    public async Task ACameraWithNoReadoutModes_RefusesASelection()
    {
        using var harness = new Harness();
        await harness.ConnectAsync();

        await harness.Session.SendAsync(new SetReadoutModeCommand(1));

        Assert.True(harness.Recorder.Last<CommandRejectedEvent>() is not null, harness.Recorder.Trail());
        Assert.Null(harness.Recorder.Last<ReadoutModeChangedEvent>());
    }

    /// <summary>
    /// The mode cannot be changed part-way through a sequence. The frames already
    /// captured were read out differently, and the fit weights every observation
    /// alike -- so mixing depths would quietly degrade the answer by an amount
    /// nothing reports.
    /// </summary>
    [Fact]
    public async Task ChangingTheReadoutModeMidSequence_IsRefused()
    {
        var options = new SimulatedMountOptions(Site, new MountMisalignment(30.0, -25.0));
        var simulated = new SimulatedMount(options);
        var mount = new RecordingMount(simulated);

        // The simulated camera has real readout modes, so this exercises the
        // mid-sequence guard rather than the no-modes one.
        var provider = new SimulatedDeviceProvider(
            StarCatalog.LoadCsv(CatalogPath), options);

        using ICamera camera = provider.OpenCamera("sim-camera");
        using var session = new AlignmentSession(camera, mount, new PerfectSolver(simulated));
        var recorder = new Recorder();
        using IDisposable subscription = session.Events.Subscribe(recorder);

        await session.SendAsync(new ConnectDeviceCommand(
            DeviceKind.Camera, AlignmentSession.AttachedProviderName, "camera"));
        await session.SendAsync(new ConnectDeviceCommand(
            DeviceKind.Mount, AlignmentSession.AttachedProviderName, "mount"));

        // Before a sequence: accepted.
        await session.SendAsync(new SetReadoutModeCommand(1));
        Assert.Equal(8, recorder.Last<ReadoutModeChangedEvent>()!.BitDepth);

        await session.SendAsync(new ConfigureSiteCommand(
            Site.LatitudeDegrees, Site.LongitudeDegrees, Site.HeightMeters));
        await session.SendAsync(new StartSessionCommand(new SessionConfiguration(5, 60.0, TimeSpan.FromSeconds(1))));

        // During one: refused.
        await session.SendAsync(new SetReadoutModeCommand(0));

        CommandRejectedEvent? rejected = recorder.Last<CommandRejectedEvent>();
        Assert.True(rejected is not null, recorder.Trail());
        Assert.Contains("sequence", rejected!.Reason, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(8, recorder.Last<ReadoutModeChangedEvent>()!.BitDepth);
    }

    private static string CatalogPath =>
        Path.Combine(AppContext.BaseDirectory, "fixtures", "tycho2_subset.csv");

    /// <summary>
    /// The camera reports its readout modes on connect, so the UI can offer them
    /// without a second round trip.
    /// </summary>
    [Fact]
    public async Task ConnectingReportsTheAvailableReadoutModes()
    {
        var options = new SimulatedMountOptions(Site, new MountMisalignment(10.0, 10.0));
        var provider = new SimulatedDeviceProvider(StarCatalog.LoadCsv(CatalogPath), options);

        using ICamera camera = provider.OpenCamera("sim-camera");
        using IMount mount = provider.OpenMount("sim-mount");
        using var session = new AlignmentSession(camera, mount, new PerfectSolver(provider.MountInstance));

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
    /// one is used and the difference is reported. A driver's site is very often
    /// a factory default or a leftover from wherever the mount was last set up,
    /// and a silent disagreement is the first sign that one of the two figures is
    /// simply wrong.
    /// </summary>
    [Fact]
    public async Task AMountReportingADifferentSite_IsNotedButNotObeyed()
    {
        using var harness = new Harness();
        await harness.ConnectAsync();

        // Half a degree of latitude away -- thirty arcminutes of pure bias in
        // the reported altitude error if it were used by mistake.
        await harness.Session.SendAsync(new ConfigureSiteCommand(Latitude + 0.5, 15.0, 200.0));

        SiteConfiguredEvent? configured = harness.Recorder.Last<SiteConfiguredEvent>();
        Assert.True(configured is not null, harness.Recorder.Trail());
        Assert.Equal(Latitude + 0.5, configured!.LatitudeDegrees, precision: 6);
        Assert.NotNull(configured.MountReportedDisagreement);
        Assert.Contains("latitude", configured.MountReportedDisagreement!, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>An agreeing mount produces no note, so that the note means something when it appears.</summary>
    [Fact]
    public async Task AMountReportingTheSameSite_ProducesNoNote()
    {
        using var harness = new Harness();
        await harness.ConnectAsync();
        await harness.ConfirmSiteAsync();

        Assert.Null(harness.Recorder.Last<SiteConfiguredEvent>()!.MountReportedDisagreement);
    }

    // ---- Manual mode ----

    /// <summary>
    /// On a mount turned by hand (D10) the engine must never command a slew, and
    /// must say what to do instead. The measurement itself does not care how the
    /// telescope reached each position.
    /// </summary>
    [Fact]
    public async Task ManualMode_NeverCommandsASlew()
    {
        using var harness = new Harness(manualMode: true);
        await harness.ReadyAsync();

        for (int i = 0; i < 3; i++)
        {
            Assert.False(harness.Recorder.Last<SlewProposedEvent>()!.RequiresMotion);
            Assert.NotNull(harness.Recorder.Last<ManualActionRequiredEvent>());

            await harness.Session.SendAsync(new CaptureNextPointCommand());
        }

        Assert.Empty(harness.Mount.Slews);
        Assert.Equal(3, harness.Camera.Exposures);
    }
}
