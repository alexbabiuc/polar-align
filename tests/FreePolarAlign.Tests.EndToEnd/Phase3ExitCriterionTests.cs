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
/// Phase 3 exit criterion: the virtual observatory must converge from an
/// injected 2° error to below 10′, then below 2′, without operator
/// intervention — and pulling a cable mid-sequence must leave a recoverable
/// state rather than a crash.
///
/// "Without operator intervention" is taken literally: the loop below reads the
/// error the software reports, turns the simulated bolts by exactly that much,
/// and measures again. Nothing anywhere is told the injected value, so if the
/// reported correction were wrong in sign or scale the loop would diverge
/// instead of converge, which is a far stronger check than comparing one
/// measurement against ground truth.
/// </summary>
public class Phase3ExitCriterionTests
{
    private static readonly string QuadDatabaseDirectory =
        Environment.GetEnvironmentVariable("FPA_QUADDB_DIR")
        ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".free-polar-align", "quaddb");

    private static StarCatalog? _catalog;

    private static StarCatalog Catalog => _catalog ??= StarCatalog.LoadCsv(
        Path.Combine(AppContext.BaseDirectory, "fixtures", "tycho2_subset.csv"));

    private static void RequireQuadDatabase() => Skip.IfNot(
        Directory.Exists(QuadDatabaseDirectory) && Directory.EnumerateFiles(QuadDatabaseDirectory, "*.qdb").Any(),
        $"No Watney quad database at '{QuadDatabaseDirectory}'. Set FPA_QUADDB_DIR, or download " +
        "watneyqdb-00-07-20-v3 per D13. Skipping the convergence test.");

    private const double Latitude = 45.0;

    private static SimulatedDeviceProvider BuildProvider(MountMisalignment injected, double coneErrorArcminutes = 20.0) =>
        new(
            Catalog,
            new SimulatedMountOptions(
                new GeodeticLocation(Latitude, 15.0, 200.0),
                injected,
                ConeErrorArcminutes: coneErrorArcminutes,
                ConePhaseDegrees: 35.0,
                Tracking: true),
            new SimulatedCameraOptions(FocalLengthMillimetres: 100.0));

    /// <summary>Collects the event stream so a sequence can be inspected after the fact.</summary>
    private sealed class Recorder : IObserver<EngineEvent>
    {
        public List<EngineEvent> Events { get; } = new();

        public void OnNext(EngineEvent value) => Events.Add(value);

        public void OnError(Exception error) { }

        public void OnCompleted() { }

        public T? Last<T>() where T : EngineEvent => Events.OfType<T>().LastOrDefault();

        /// <summary>
        /// The whole sequence as text, for assertion messages. A bare
        /// "expected not null" tells you nothing about which step went wrong in
        /// a twelve-command sequence; the narrated stream tells you exactly.
        /// </summary>
        public string Trail() => Environment.NewLine + string.Join(
            Environment.NewLine,
            Events.Select(e => $"  {EngineEventNarrator.Describe(e).Severity}: {EngineEventNarrator.Describe(e).Message}")
                .Where(line => !line.EndsWith(": ", StringComparison.Ordinal)));
    }

    private static readonly GeodeticLocation Site = new(Latitude, 15.0, 200.0);

    /// <summary>
    /// Connects the devices and confirms the site, which the engine now requires
    /// as explicit acts before a sequence can start (D18, D19). A program driving
    /// the engine has to do exactly what the UI does, which is the point of the
    /// boundary being message-shaped.
    /// </summary>
    private static async Task ConnectAndConfirmSiteAsync(AlignmentSession session)
    {
        await session.SendAsync(new ConnectDeviceCommand(
            DeviceKind.Camera, AlignmentSession.AttachedProviderName, "camera"));
        await session.SendAsync(new ConnectDeviceCommand(
            DeviceKind.Mount, AlignmentSession.AttachedProviderName, "mount"));
        await session.SendAsync(new ConfigureSiteCommand(
            Site.LatitudeDegrees, Site.LongitudeDegrees, Site.HeightMeters));
    }

    private static async Task<(AlignmentEstimate? Estimate, Recorder Recorder)> RunSequenceAsync(
        AlignmentSession session, int captureCount)
    {
        var recorder = new Recorder();
        using IDisposable subscription = session.Events.Subscribe(recorder);

        await session.SendAsync(new StartSessionCommand(new SessionConfiguration(
            CapturePoints: captureCount,
            RequestedSweepDegrees: 70.0,
            ExposureDuration: TimeSpan.FromSeconds(2))));

        if (recorder.Last<SessionFaultedEvent>() is not null || recorder.Last<CommandRejectedEvent>() is not null)
        {
            return (null, recorder);
        }

        // D18: the engine proposes the first point rather than slewing to it,
        // and that first point is wherever the telescope already happens to be.
        Assert.NotNull(recorder.Last<SlewProposedEvent>());
        Assert.False(recorder.Last<SlewProposedEvent>()!.RequiresMotion);

        // Driving the sequence by repeated commands is what "no operator
        // intervention" looks like from the engine's side: the caller is a
        // program, and the engine only ever answers with events (D6).
        for (int i = 0; i < captureCount + 1; i++)
        {
            await session.SendAsync(new CaptureNextPointCommand());
            if (recorder.Last<SessionFaultedEvent>() is not null || recorder.Last<SessionCompletedEvent>() is not null)
            {
                break;
            }
        }

        return (recorder.Last<AlignmentUpdatedEvent>()?.Estimate, recorder);
    }

    private static double AxisErrorArcminutes(SimulatedMount mount)
    {
        HorizontalCoordinates axis = mount.TruePolarAxis;
        HorizontalCoordinates pole = MountForwardModel.NominalPole(Latitude);
        return SeparationArcminutes(axis, pole);
    }

    /// <summary>
    /// The exit criterion. Start two degrees out, and converge by acting only on
    /// what the software reports.
    /// </summary>
    [SkippableFact]
    public async Task VirtualObservatory_ConvergesFromTwoDegrees_BelowTenThenTwoArcminutes()
    {
        RequireQuadDatabase();

        // Two degrees of total error, split across both axes. Note the azimuth
        // figure is larger than the altitude one for an even split: an azimuth
        // *angle* subtends less on the sky by cos(axis altitude) (D12), so 120'
        // of azimuth is only 85' of pointing error at this latitude.
        var injected = new MountMisalignment(AltitudeErrorArcminutes: 84.85, AzimuthErrorArcminutes: 120.0);
        SimulatedDeviceProvider provider = BuildProvider(injected);
        SimulatedMount mount = provider.MountInstance;

        double startingError = AxisErrorArcminutes(mount);
        Assert.InRange(startingError, 115.0, 125.0);

        using ICamera camera = provider.OpenCamera("sim-camera");
        using var solver = new WatneyPlateSolver(QuadDatabaseDirectory);
        using var session = new AlignmentSession(camera, mount, solver, new AlignmentSessionOptions(
            CaptureCount: 6, SweepDegrees: 70.0, ExpectedSolveNoiseArcseconds: 3.0));

        await ConnectAndConfirmSiteAsync(session);

        var errors = new List<double> { startingError };
        bool below10 = false;
        bool below2 = false;

        for (int round = 0; round < 4; round++)
        {
            var (estimate, recorder) = await RunSequenceAsync(session, 6);

            Assert.True(estimate is not null, recorder.Trail());
            Assert.Null(recorder.Last<AlignmentWithheldEvent>());

            // Turn the bolts by exactly what the software asked for.
            mount.AdjustAxis(-estimate!.AltitudeErrorArcminutes, -estimate.AzimuthErrorArcminutes);

            double remaining = AxisErrorArcminutes(mount);
            errors.Add(remaining);

            if (remaining < 10.0)
            {
                below10 = true;
            }

            if (below10 && remaining < 2.0)
            {
                below2 = true;
                break;
            }
        }

        string trail = string.Join(" -> ", errors.Select(e => $"{e:F2}'"));
        Assert.True(below10, $"never reached 10': {trail}");
        Assert.True(below2, $"never reached 2': {trail}");

        // Convergence, not luck: the error must have shrunk monotonically.
        for (int i = 1; i < errors.Count; i++)
        {
            Assert.True(errors[i] < errors[i - 1],
                $"round {i} made the alignment worse, not better: {trail}");
        }
    }

    /// <summary>
    /// Cone error must not affect convergence any more than it affects a single
    /// fit. A guide scope squared up by hand can be a degree off.
    /// </summary>
    [SkippableFact]
    public async Task Convergence_IsUnaffectedByLargeConeError()
    {
        RequireQuadDatabase();

        var injected = new MountMisalignment(60.0, -80.0);
        SimulatedDeviceProvider provider = BuildProvider(injected, coneErrorArcminutes: 90.0);
        SimulatedMount mount = provider.MountInstance;

        using ICamera camera = provider.OpenCamera("sim-camera");
        using var solver = new WatneyPlateSolver(QuadDatabaseDirectory);
        using var session = new AlignmentSession(camera, mount, solver,
            new AlignmentSessionOptions(CaptureCount: 6, SweepDegrees: 70.0, ExpectedSolveNoiseArcseconds: 3.0));

        await ConnectAndConfirmSiteAsync(session);

        var (estimate, recorder) = await RunSequenceAsync(session, 6);
        Assert.True(estimate is not null, recorder.Trail());

        mount.AdjustAxis(-estimate!.AltitudeErrorArcminutes, -estimate.AzimuthErrorArcminutes);
        Assert.True(AxisErrorArcminutes(mount) < 10.0,
            $"one round with 90' of cone error left {AxisErrorArcminutes(mount):F2}'");
    }

    /// <summary>
    /// The cable-pull test. A device vanishing mid-sequence must produce a
    /// reported fault and a session that can simply be started again — not an
    /// unhandled exception, and not a wedged engine.
    /// </summary>
    [SkippableFact]
    public async Task DeviceLostMidSequence_LeavesARecoverableSession()
    {
        RequireQuadDatabase();

        SimulatedDeviceProvider provider = BuildProvider(new MountMisalignment(30.0, -25.0));
        SimulatedMount mount = provider.MountInstance;

        using ICamera camera = provider.OpenCamera("sim-camera");
        using var solver = new WatneyPlateSolver(QuadDatabaseDirectory);
        using var session = new AlignmentSession(camera, mount, solver,
            new AlignmentSessionOptions(CaptureCount: 6, SweepDegrees: 70.0, ExpectedSolveNoiseArcseconds: 3.0));

        var recorder = new Recorder();
        using IDisposable subscription = session.Events.Subscribe(recorder);

        await ConnectAndConfirmSiteAsync(session);
        await session.SendAsync(new StartSessionCommand(new SessionConfiguration(
            6, 70.0, TimeSpan.FromSeconds(2))));
        await session.SendAsync(new CaptureNextPointCommand());
        Assert.NotNull(recorder.Last<PointCapturedEvent>());

        // Pull the cable.
        mount.SimulateDisconnection();
        ((SimulatedCamera)camera).SimulateDisconnection();

        await session.SendAsync(new CaptureNextPointCommand());

        SessionFaultedEvent? fault = recorder.Last<SessionFaultedEvent>();
        Assert.NotNull(fault);
        Assert.False(string.IsNullOrWhiteSpace(fault!.Reason));

        // Recoverable: reconnect and the very next session must run normally.
        await mount.ConnectAsync();
        await camera.ConnectAsync();

        var (estimate, second) = await RunSequenceAsync(session, 6);
        Assert.True(estimate is not null, second.Trail());
        Assert.Null(second.Last<SessionFaultedEvent>());
    }

    /// <summary>
    /// Manual mode (D10): the algorithm does not care how the mount reached each
    /// position, so prompting the operator must produce the same measurement a
    /// slew would. Here the "operator" obeys the prompt by driving the simulated
    /// mount, which is what a real one does with the bolts and knobs.
    /// </summary>
    [SkippableFact]
    public async Task ManualMode_PromptsAndMeasures()
    {
        RequireQuadDatabase();

        var injected = new MountMisalignment(40.0, -30.0);
        SimulatedDeviceProvider provider = BuildProvider(injected);
        SimulatedMount mount = provider.MountInstance;

        using ICamera camera = provider.OpenCamera("sim-camera");
        using var solver = new WatneyPlateSolver(QuadDatabaseDirectory);
        using var session = new AlignmentSession(camera, mount, solver, new AlignmentSessionOptions(
            CaptureCount: 5, SweepDegrees: 70.0, ManualMode: true, ExpectedSolveNoiseArcseconds: 3.0));

        var recorder = new Recorder();
        using IDisposable subscription = session.Events.Subscribe(recorder);

        await ConnectAndConfirmSiteAsync(session);
        await session.SendAsync(new StartSessionCommand(new SessionConfiguration(
            5, 70.0, TimeSpan.FromSeconds(2))));

        // The prompt now arrives with the proposal, so the operator sees where
        // to go before pressing anything -- one press per point rather than one
        // to ask and another to proceed.
        for (int i = 0; i < 5; i++)
        {
            SlewProposedEvent? proposed = recorder.Last<SlewProposedEvent>();
            Assert.NotNull(proposed);
            Assert.False(proposed!.RequiresMotion, "manual mode must never ask the engine to slew (D10)");

            ManualActionRequiredEvent? prompt = recorder.Last<ManualActionRequiredEvent>();
            Assert.NotNull(prompt);
            Assert.Contains("declination", prompt!.Instruction, StringComparison.OrdinalIgnoreCase);

            // The operator obeys the prompt, turning the mount to the position
            // it named. The engine is told nothing about this: as far as it
            // knows, the telescope simply is where it is.
            await mount.SlewToCoordinatesAsync(proposed.RaDegrees, proposed.DecDegrees);

            await session.SendAsync(new CaptureNextPointCommand());
            Assert.Null(recorder.Last<SessionFaultedEvent>());
        }

        AlignmentUpdatedEvent? updated = recorder.Last<AlignmentUpdatedEvent>();
        Assert.True(updated is not null, recorder.Trail());

        mount.AdjustAxis(-updated!.Estimate.AltitudeErrorArcminutes, -updated.Estimate.AzimuthErrorArcminutes);
        Assert.True(AxisErrorArcminutes(mount) < 10.0,
            $"manual mode left {AxisErrorArcminutes(mount):F2}' after one round");
    }

    private static double SeparationArcminutes(HorizontalCoordinates a, HorizontalCoordinates b)
    {
        double d2r = Math.PI / 180.0;
        double lat1 = a.AltitudeDegrees * d2r, lat2 = b.AltitudeDegrees * d2r;
        double dLat = lat2 - lat1;
        double dLon = (b.AzimuthDegrees - a.AzimuthDegrees) * d2r;

        double h = Math.Sin(dLat / 2.0) * Math.Sin(dLat / 2.0)
                 + Math.Cos(lat1) * Math.Cos(lat2) * Math.Sin(dLon / 2.0) * Math.Sin(dLon / 2.0);
        return 2.0 * Math.Asin(Math.Sqrt(Math.Clamp(h, 0.0, 1.0))) * 180.0 / Math.PI * 60.0;
    }
}
