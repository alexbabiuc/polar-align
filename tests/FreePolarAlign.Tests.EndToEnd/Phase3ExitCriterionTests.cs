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

    /// <summary>
    /// D26's delays, shortened: the simulated camera renders a frame in a
    /// fraction of a second and the simulated mount settles instantly, so the
    /// seconds a real mount needs would only make the suite slow.
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
    /// Runs one sequence the way a user at the mount would: confirm each slew the
    /// engine proposes, and otherwise wait while it samples (D26). "Without
    /// operator intervention" is what that looks like from the engine's side:
    /// the caller is a program, and the engine only ever answers with events (D6).
    /// </summary>
    private static async Task<(AlignmentEstimate? Estimate, Recorder Recorder)> RunSequenceAsync(
        AlignmentSession session, int captureCount)
    {
        var recorder = new Recorder();
        using IDisposable subscription = session.Events.Subscribe(recorder);

        await session.SendAsync(new StartSessionCommand(new SessionConfiguration(
            CapturePoints: captureCount,
            RequestedSweepDegrees: 70.0)));

        if (recorder.Last<SessionFaultedEvent>() is not null || recorder.Last<CommandRejectedEvent>() is not null)
        {
            return (null, recorder);
        }

        int seen = 0;
        DateTime deadline = DateTime.UtcNow + TimeSpan.FromMinutes(3);
        while (DateTime.UtcNow < deadline)
        {
            IReadOnlyList<EngineEvent> events = recorder.Events;
            for (; seen < events.Count; seen++)
            {
                switch (events[seen])
                {
                    case SessionCompletedEvent:
                    case SessionFaultedEvent:
                        return (recorder.Last<AlignmentUpdatedEvent>()?.Estimate, recorder);
                    case SlewProposedEvent { RequiresMotion: true }:
                        await session.SendAsync(new CaptureNextPointCommand());
                        break;
                }
            }

            await session.SendAsync(new RefreshMountStatusCommand());
            await Task.Delay(50);
        }

        return (null, recorder);
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
        using var session = new AlignmentSession(camera, mount, solver, LiveOptions(6, TempDirectory()));

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
        using var session = new AlignmentSession(camera, mount, solver, LiveOptions(6, TempDirectory()));

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
        using var session = new AlignmentSession(camera, mount, solver, LiveOptions(6, TempDirectory()));

        var recorder = new Recorder();
        using IDisposable subscription = session.Events.Subscribe(recorder);

        await ConnectAndConfirmSiteAsync(session);
        await session.SendAsync(new StartSessionCommand(new SessionConfiguration(6, 70.0)));
        await session.SendAsync(new CaptureNextPointCommand());
        await recorder.WaitForAsync<PointCapturedEvent>(timeout: TimeSpan.FromMinutes(1));

        // Pull the cable.
        mount.SimulateDisconnection();
        ((SimulatedCamera)camera).SimulateDisconnection();

        SessionFaultedEvent fault = await recorder.WaitForAsync<SessionFaultedEvent>(
            poll: () => session.SendAsync(new RefreshMountStatusCommand()).AsTask());
        Assert.False(string.IsNullOrWhiteSpace(fault.Reason));

        // Recoverable: reconnect as the UI would, and a new sequence must start
        // and sample again rather than finding the engine wedged.
        //
        // Deliberately not driven to a full six-point solution. This test is
        // about a device vanishing, and requiring a whole sequence to solve
        // would import the solver's reliability into it -- which, with the
        // committed sample catalogue, depends on which patch of sky the current
        // sidereal time happens to select (see the note in ROADMAP.md). A test
        // that fails depending on the hour tells you nothing about the thing it
        // is named after.
        await mount.ConnectAsync();
        await camera.ConnectAsync();

        var recovered = new Recorder();
        using IDisposable recoveredSubscription = session.Events.Subscribe(recovered);

        await ConnectAndConfirmSiteAsync(session);
        await session.SendAsync(new StartSessionCommand(new SessionConfiguration(6, 70.0)));

        Assert.True(recovered.Last<SessionStartedEvent>() is not null, recovered.Trail());
        Assert.Null(recovered.Last<CommandRejectedEvent>());

        await session.SendAsync(new CaptureNextPointCommand());
        await recovered.WaitForAsync<PointCapturedEvent>(timeout: TimeSpan.FromMinutes(1));
        Assert.Null(recovered.Last<SessionFaultedEvent>());
    }

    /// <summary>
    /// Phase 4d, exit criterion 2: with no mount connected (D10 as revised), a
    /// sequence turned by hand is measured through blind solves of rendered
    /// frames, and the result is good enough to act on. The engine is never told
    /// the mount exists; the "operator" turns it by driving the simulated mount
    /// directly, which is what a real one does with the RA clutch.
    /// </summary>
    [SkippableFact]
    public async Task Unconnected_AHandTurnedSweepMeasuresTheMisalignment()
    {
        RequireQuadDatabase();

        var injected = new MountMisalignment(40.0, -30.0);
        SimulatedDeviceProvider provider = BuildProvider(injected);
        SimulatedMount mount = provider.MountInstance;
        await mount.ConnectAsync();

        using ICamera camera = provider.OpenCamera("sim-camera");
        using var solver = new WatneyPlateSolver(QuadDatabaseDirectory);
        using var session = new AlignmentSession(camera, mount: null, solver, LiveOptions(5, TempDirectory()));

        var recorder = new Recorder();
        using IDisposable subscription = session.Events.Subscribe(recorder);

        await session.SendAsync(new ConnectDeviceCommand(DeviceKind.Camera, AlignmentSession.AttachedProviderName, "camera"));
        await session.SendAsync(new ConfigureSiteCommand(Site.LatitudeDegrees, Site.LongitudeDegrees, Site.HeightMeters));
        await session.SendAsync(new StartSessionCommand(new SessionConfiguration(5, 70.0)));

        Assert.Equal(SequenceMode.Unconnected, recorder.Last<SessionStartedEvent>()!.Mode);
        ManualActionRequiredEvent prompt = recorder.Last<ManualActionRequiredEvent>()!;
        Assert.Contains("declination", prompt.Instruction, StringComparison.OrdinalIgnoreCase);

        // The operator sets declination as told, then turns RA a spacing at a
        // time, waiting for each sample before moving on.
        double declination = recorder.Last<TargetSelectedEvent>()!.DeclinationDegrees;
        double rotation = recorder.Last<SlewProposedEvent>()!.MechanicalRotationDegrees;
        double spacing = 70.0 / 4;

        for (int point = 1; point <= 5; point++)
        {
            (double ra, double dec) = TargetSelection.ResolveCommand(Site, rotation, declination, DateTime.UtcNow);
            await mount.SlewToCoordinatesAsync(ra, dec);

            await recorder.WaitForAsync<PointCapturedEvent>(e => e.Point.Index == point, timeout: TimeSpan.FromMinutes(1));
            rotation += spacing;
        }

        await recorder.WaitForAsync<SessionCompletedEvent>();
        AlignmentUpdatedEvent? updated = recorder.Last<AlignmentUpdatedEvent>();
        Assert.True(updated is not null, recorder.Trail());

        // Within 1', Phase 2's end-to-end standard -- the Phase 4d bar.
        Assert.Equal(injected.AltitudeErrorArcminutes, updated!.Estimate.AltitudeErrorArcminutes, tolerance: 1.0);
        Assert.Equal(injected.AzimuthErrorArcminutes, updated.Estimate.AzimuthErrorArcminutes, tolerance: 1.0);
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
