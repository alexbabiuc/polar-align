using FreePolarAlign.Core.Alignment;
using FreePolarAlign.Core.Engine;
using FreePolarAlign.Devices;
using FreePolarAlign.Devices.Simulated;
using FreePolarAlign.Devices.Simulated.SyntheticSky;
using FreePolarAlign.Session;
using FreePolarAlign.Solving;
using FreePolarAlign.Tests.EndToEnd.Scenarios;
using FreePolarAlign.Tests.Support;
using Xunit;

namespace FreePolarAlign.Tests.EndToEnd;

/// <summary>
/// The whole pipeline on real pixels: rendered frames, the real Watney solver,
/// the engine's sweep and its live reading, measured against the misalignment
/// the simulated mount actually has.
///
/// Each test is a recorded scenario (see <see cref="ScenarioCamera"/>). It
/// starts at a fixed instant on the test clock, the engine plans its own sweep
/// from there, and the bolts are turned by amounts fixed in advance rather than
/// by what the engine reports. That makes every frame the same on every run, so
/// each is rendered once and kept, and a run costs only its solves. What is
/// checked is unchanged: after every sweep and every turn, the engine's figure
/// must match the truth.
///
/// Turning by fixed amounts gives up the earlier closed loop, in which the
/// bolts were turned by whatever the engine said. That loop's real claim -- the
/// reported correction has the right sign and scale -- is checked here
/// directly, against the truth, after each turn.
///
/// The cable-pull test that also lived here needs no real pixels and is now
/// with the fast tests (<see cref="ConfirmBeforeSlewTests"/>).
/// </summary>
public class Phase3ExitCriterionTests
{
    private static readonly string QuadDatabaseDirectory =
        Environment.GetEnvironmentVariable("FPA_QUADDB_DIR")
        ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".free-polar-align", "quaddb");

    private const double Latitude = 45.0;

    private static readonly GeodeticLocation Site = new(Latitude, 15.0, 200.0);

    /// <summary>Phase 2's end-to-end standard, per axis.</summary>
    private const double ToleranceArcminutes = 1.0;

    /// <summary>
    /// How much fake time a sweep may take before the test gives up. Generous:
    /// a sweep here takes seconds of the sky's time, and the guard only has to
    /// catch one that never ends.
    /// </summary>
    private static readonly TimeSpan SequenceLimit = TimeSpan.FromMinutes(10);

    private static void RequireQuadDatabase() => Skip.IfNot(
        Directory.Exists(QuadDatabaseDirectory) && Directory.EnumerateFiles(QuadDatabaseDirectory, "*.qdb").Any(),
        $"No Watney quad database at '{QuadDatabaseDirectory}'. Set FPA_QUADDB_DIR, or download " +
        "watneyqdb-00-07-20-v3 per D13.");

    /// <summary>
    /// D26's delays, shortened: on the test clock they cost nothing either way,
    /// but they keep a scenario's fake timeline short.
    /// </summary>
    internal static AlignmentSessionOptions LiveOptions(int captureCount, string failedSolvesDirectory) => new(
        CaptureCount: captureCount,
        SweepDegrees: 70.0,
        ExposureDuration: TimeSpan.FromMilliseconds(100),
        ExpectedSolveNoiseArcseconds: 3.0,
        SettleDelay: TimeSpan.FromMilliseconds(50),
        SolveInterval: TimeSpan.FromMilliseconds(200),
        FailedSolvesDirectory: failedSolvesDirectory);

    internal static string TempDirectory() => Path.Combine(Path.GetTempPath(), $"fpa-failed-{Guid.NewGuid():N}");

    /// <summary>
    /// Counts the solves in flight. A real solve takes real seconds, and fake
    /// time must not run on while one does, or the sky would turn between a
    /// frame and its result -- and a replay would no longer match its recording.
    /// </summary>
    private sealed class CountingSolver(ISolver inner) : ISolver, IDisposable
    {
        private int _inFlight;

        public string Name => inner.Name;

        public bool Busy => Volatile.Read(ref _inFlight) > 0;

        public async Task<PlateSolveResult> SolveAsync(PlateSolveRequest request, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _inFlight);
            try
            {
                return await inner.SolveAsync(request, cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                Interlocked.Decrement(ref _inFlight);
            }
        }

        public void Dispose() => (inner as IDisposable)?.Dispose();
    }

    /// <summary>One scenario's simulated mount, kept frames, solver and engine, all on one test clock.</summary>
    private sealed class Rig : IDisposable
    {
        private readonly IDisposable _subscription;

        public Rig(string scenario, MountMisalignment injected, double coneErrorArcminutes, int captureCount, bool connectMount)
        {
            RequireQuadDatabase();

            Clock = new TestClock();
            Mount = new SimulatedMount(
                new SimulatedMountOptions(
                    Site, injected,
                    ConeErrorArcminutes: coneErrorArcminutes,
                    ConePhaseDegrees: 35.0,
                    Tracking: true,
                    InitialMechanicalRotationDegrees: -70.0),
                Clock);
            Camera = ScenarioCamera.Open(
                scenario, Mount, new SimulatedCameraOptions(FocalLengthMillimetres: 100.0),
                () => StarCatalog.LoadCsv(Path.Combine(AppContext.BaseDirectory, "fixtures", "tycho2_subset.csv")));
            Skip.If(!Camera.HasFrames && !ScenarioCamera.Recording,
                $"Scenario '{scenario}' has no recorded frames. {FrameRecording.HowToRecord}");

            Solver = new CountingSolver(new WatneyPlateSolver(QuadDatabaseDirectory, Clock));
            Session = new AlignmentSession(
                Camera, connectMount ? Mount : null, Solver, Clock, LiveOptions(captureCount, TempDirectory()));
            Recorder = new Recorder(Clock) { Busy = () => Solver.Busy || Camera.Busy };
            _subscription = Session.Events.Subscribe(Recorder);
        }

        public TestClock Clock { get; }

        public SimulatedMount Mount { get; }

        public ScenarioCamera Camera { get; }

        public CountingSolver Solver { get; }

        public AlignmentSession Session { get; }

        public Recorder Recorder { get; }

        public Task PollAsync() => Session.SendAsync(new RefreshMountStatusCommand()).AsTask();

        /// <summary>
        /// Connects what the engine is to know about and confirms the site,
        /// which it requires before a sequence (D18, D19).
        /// </summary>
        public async Task ConnectAsync(bool mount)
        {
            await Mount.ConnectAsync();
            await Session.SendAsync(new ConnectDeviceCommand(DeviceKind.Camera, AlignmentSession.AttachedProviderName, "camera"));
            if (mount)
            {
                await Session.SendAsync(new ConnectDeviceCommand(DeviceKind.Mount, AlignmentSession.AttachedProviderName, "mount"));
            }

            await Session.SendAsync(new ConfigureSiteCommand(Site.LatitudeDegrees, Site.LongitudeDegrees, Site.HeightMeters));
        }

        /// <summary>
        /// Runs one connected sweep the way a user at the mount would: confirm
        /// each slew the engine proposes, otherwise wait while it samples (D26),
        /// until the live reading takes over (D29).
        /// </summary>
        public async Task<AlignmentEstimate> SweepAsync(int captureCount)
        {
            int seen = Recorder.Count;
            await Session.SendAsync(new StartSessionCommand(new SessionConfiguration(captureCount, 70.0)));

            DateTimeOffset deadline = Clock.GetUtcNow() + SequenceLimit;
            while (true)
            {
                Assert.True(Clock.GetUtcNow() < deadline, $"the sweep never finished.{Recorder.Trail()}");
                Assert.Empty(Camera.Misses);

                IReadOnlyList<EngineEvent> events = Recorder.Events;
                for (; seen < events.Count; seen++)
                {
                    switch (events[seen])
                    {
                        case TrackingStartedEvent:
                            AlignmentUpdatedEvent? updated = Recorder.Last<AlignmentUpdatedEvent>();
                            Assert.True(updated is not null, Recorder.Trail());
                            return updated!.Estimate;
                        case SessionFaultedEvent fault:
                            Assert.Fail($"the sweep faulted: {fault.Reason}{Recorder.Trail()}");
                            break;
                        case SlewProposedEvent { RequiresMotion: true }:
                            await Session.SendAsync(new CaptureNextPointCommand());
                            break;
                    }
                }

                await PollAsync();
                await Clock.PassAsync(TimeSpan.FromMilliseconds(50), busy: Recorder.Busy);
            }
        }

        /// <summary>
        /// A live reading wholly from after now. The first one published after
        /// it is skipped, because its solve may have looked at the sky a moment
        /// before.
        /// </summary>
        public async Task<AlignmentTrackedEvent> FreshReadingAsync()
        {
            DateTimeOffset turnedAt = Clock.GetUtcNow();
            AlignmentTrackedEvent straddling = await Recorder.WaitForAsync<AlignmentTrackedEvent>(
                e => e.TimestampUtc > turnedAt, poll: PollAsync, timeout: TimeSpan.FromMinutes(1));
            return await Recorder.WaitForAsync<AlignmentTrackedEvent>(
                e => e.TimestampUtc > straddling.TimestampUtc && e.IsReliable, poll: PollAsync, timeout: TimeSpan.FromMinutes(1));
        }

        /// <summary>The engine's figure must be the mount's real misalignment, per axis.</summary>
        public void AssertMatchesTruth(AlignmentEstimate estimate, string when)
        {
            MountMisalignment truth = Mount.CurrentMisalignment;
            Assert.True(
                Math.Abs(estimate.AltitudeErrorArcminutes - truth.AltitudeErrorArcminutes) < ToleranceArcminutes &&
                Math.Abs(estimate.AzimuthErrorArcminutes - truth.AzimuthErrorArcminutes) < ToleranceArcminutes,
                $"{when}: read {estimate.AltitudeErrorArcminutes:F2}' / {estimate.AzimuthErrorArcminutes:F2}', " +
                $"truth {truth.AltitudeErrorArcminutes:F2}' / {truth.AzimuthErrorArcminutes:F2}'");
        }

        public void Dispose()
        {
            _subscription.Dispose();
            Session.Dispose();
            Solver.Dispose();
            Camera.Dispose();
            Mount.Dispose();
        }
    }

    /// <summary>
    /// One sweep, then the live reading through three bolt turns. Two degrees
    /// out, with 90' of cone error, which must make no difference (D11).
    ///
    /// The azimuth figure is larger than the altitude one for an even split: an
    /// azimuth *angle* subtends less on the sky by cos(axis altitude) (D12), so
    /// 120' of azimuth is only 85' of pointing error at this latitude.
    /// </summary>
    [SkippableFact]
    public async Task SweepThenLiveAdjustments_ReadTheTruthAfterEveryTurn()
    {
        var injected = new MountMisalignment(AltitudeErrorArcminutes: 84.85, AzimuthErrorArcminutes: 120.0);
        using var rig = new Rig("sweep-then-live", injected, coneErrorArcminutes: 90.0, captureCount: 5, connectMount: true);
        await rig.ConnectAsync(mount: true);

        AlignmentEstimate sweep = await rig.SweepAsync(5);
        rig.AssertMatchesTruth(sweep, "after the sweep");

        // Turns fixed in advance, as fractions of what was injected: most of it,
        // then most of the rest, then nearly all that is left.
        foreach (double fraction in new[] { 0.6, 0.3, 0.09 })
        {
            rig.Mount.AdjustAxis(
                -fraction * injected.AltitudeErrorArcminutes, -fraction * injected.AzimuthErrorArcminutes);

            AlignmentTrackedEvent reading = await rig.FreshReadingAsync();
            rig.AssertMatchesTruth(reading.Estimate, $"after turning {fraction:P0} of the error out");
        }

        Assert.Empty(rig.Camera.Misses);
        Assert.Single(rig.Recorder.All<TrackingStartedEvent>());
    }

    /// <summary>
    /// Three sweeps, of three, four and five points, with the bolts turned
    /// between them. Each sweep must measure what is left. Three points is the
    /// fewest a fit takes (D7), and the sweep is the same 70°, so the smaller
    /// sweeps are the harder ones.
    /// </summary>
    [SkippableFact]
    public async Task RepeatedSweeps_OfThreeFourAndFivePoints_EachMeasureWhatIsLeft()
    {
        var injected = new MountMisalignment(AltitudeErrorArcminutes: 60.0, AzimuthErrorArcminutes: -80.0);
        using var rig = new Rig("repeated-sweeps", injected, coneErrorArcminutes: 20.0, captureCount: 5, connectMount: true);
        await rig.ConnectAsync(mount: true);

        int[] points = { 3, 4, 5 };
        double[] turnedOutAfter = { 0.7, 0.25 };
        for (int round = 0; round < points.Length; round++)
        {
            AlignmentEstimate estimate = await rig.SweepAsync(points[round]);
            rig.AssertMatchesTruth(estimate, $"sweep {round + 1}, of {points[round]} points");

            if (round < turnedOutAfter.Length)
            {
                rig.Mount.AdjustAxis(
                    -turnedOutAfter[round] * injected.AltitudeErrorArcminutes,
                    -turnedOutAfter[round] * injected.AzimuthErrorArcminutes);
            }
        }

        Assert.Empty(rig.Camera.Misses);
    }

    /// <summary>
    /// Phase 4d, exit criterion 2: with no mount connected (D10 as revised), a
    /// sequence turned by hand is measured through blind solves, and the result
    /// is good enough to act on. The engine is never told the mount exists; the
    /// "operator" turns it by driving the simulated mount directly, which is
    /// what a real one does with the RA clutch.
    /// </summary>
    [SkippableFact]
    public async Task Unconnected_AHandTurnedSweepMeasuresTheMisalignment()
    {
        var injected = new MountMisalignment(40.0, -30.0);
        using var rig = new Rig("unconnected", injected, coneErrorArcminutes: 20.0, captureCount: 5, connectMount: false);
        await rig.ConnectAsync(mount: false);

        await rig.Session.SendAsync(new StartSessionCommand(new SessionConfiguration(5, 70.0)));

        Assert.Equal(SequenceMode.Unconnected, rig.Recorder.Last<SessionStartedEvent>()!.Mode);
        ManualActionRequiredEvent prompt = rig.Recorder.Last<ManualActionRequiredEvent>()!;
        Assert.Contains("declination", prompt.Instruction, StringComparison.OrdinalIgnoreCase);

        // The operator sets declination as told, then turns RA a spacing at a
        // time, waiting for each sample before moving on.
        double declination = rig.Recorder.Last<TargetSelectedEvent>()!.DeclinationDegrees;
        double rotation = rig.Recorder.Last<SlewProposedEvent>()!.MechanicalRotationDegrees;
        double spacing = 70.0 / 4;

        for (int point = 1; point <= 5; point++)
        {
            (double ra, double dec) = TargetSelection.ResolveCommand(Site, rotation, declination, rig.Clock.UtcNow);
            await rig.Mount.SlewToCoordinatesAsync(ra, dec);

            await rig.Recorder.WaitForAsync<PointCapturedEvent>(e => e.Point.Index == point, timeout: TimeSpan.FromMinutes(1));
            rotation += spacing;
        }

        await rig.Recorder.WaitForAsync<TrackingStartedEvent>();
        AlignmentUpdatedEvent? updated = rig.Recorder.Last<AlignmentUpdatedEvent>();
        Assert.True(updated is not null, rig.Recorder.Trail());
        rig.AssertMatchesTruth(updated!.Estimate, "after the hand-turned sweep");
        Assert.Empty(rig.Camera.Misses);
    }
}
