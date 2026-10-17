using FreePolarAlign.Core.Alignment;
using FreePolarAlign.Core.Astrometry;
using FreePolarAlign.Core.Engine;
using FreePolarAlign.Devices;
using FreePolarAlign.Devices.Simulated;
using FreePolarAlign.Session;
using FreePolarAlign.Solving;
using FreePolarAlign.Tests.Support;

namespace FreePolarAlign.Tests.EndToEnd;

/// <summary>
/// Collects the event stream. Locked, because the engine now publishes from its
/// capture loop, solves and slews as well as from commands (D26), and a test
/// reads the list while those are still running.
/// </summary>
internal sealed class Recorder : IObserver<EngineEvent>
{
    private readonly List<EngineEvent> _events = new();
    private readonly TestClock _clock;

    /// <param name="clock">The engine's clock, which waiting moves on (D37).</param>
    public Recorder(TestClock clock) => _clock = clock;

    /// <summary>
    /// True while real work is in flight, such as a solve. Waiting does not move
    /// time on until it is done (see <see cref="TestClock.RunUntilAsync{T}"/>).
    /// </summary>
    public Func<bool>? Busy { get; set; }

    public IReadOnlyList<EngineEvent> Events
    {
        get
        {
            lock (_events)
            {
                return _events.ToArray();
            }
        }
    }

    public int Count
    {
        get
        {
            lock (_events)
            {
                return _events.Count;
            }
        }
    }

    public void OnNext(EngineEvent value)
    {
        lock (_events)
        {
            _events.Add(value);
        }
    }

    public void OnError(Exception error) { }

    public void OnCompleted() { }

    public T? Last<T>() where T : EngineEvent => Events.OfType<T>().LastOrDefault();

    public IEnumerable<T> All<T>() where T : EngineEvent => Events.OfType<T>();

    public int IndexOfLast<T>() where T : EngineEvent
    {
        IReadOnlyList<EngineEvent> events = Events;
        for (int i = events.Count - 1; i >= 0; i--)
        {
            if (events[i] is T)
            {
                return i;
            }
        }

        return -1;
    }

    /// <summary>
    /// The whole stream as text, for assertion messages. A bare "expected not
    /// null" says nothing about which step of a sequence went wrong; the
    /// narrated stream says exactly.
    /// </summary>
    public string Trail() => Environment.NewLine + string.Join(
        Environment.NewLine,
        Events.Select(e => EngineEventNarrator.Describe(e))
            .Where(n => !string.IsNullOrEmpty(n.Message))
            .Select(n => $"  {n.Severity}: {n.Message}"));

    /// <summary>
    /// Waits for an event matching <paramref name="predicate"/> that arrived at
    /// or after <paramref name="fromIndex"/>, moving the fake clock on between
    /// looks and calling <paramref name="poll"/> -- which is how a test stands in
    /// for the UI's status timer.
    /// </summary>
    /// <param name="timeout">How much fake time may pass first.</param>
    public Task<T> WaitForAsync<T>(
        Func<T, bool>? predicate = null,
        int fromIndex = 0,
        Func<Task>? poll = null,
        TimeSpan? timeout = null) where T : EngineEvent =>
        _clock.RunUntilAsync(
            () =>
            {
                IReadOnlyList<EngineEvent> events = Events;
                for (int i = fromIndex; i < events.Count; i++)
                {
                    if (events[i] is T match && (predicate is null || predicate(match)))
                    {
                        return match;
                    }
                }

                return null;
            },
            () => $" No {typeof(T).Name} arrived.{Trail()}",
            timeout,
            poll,
            Busy);
}

/// <summary>
/// Wraps the real simulated mount and records every slew the *engine* asks for.
/// The recording is the point: "nothing moved" is only a meaningful assertion if
/// movement is observable. A test moving the simulated mount directly, as a
/// hand controller would, goes around this and is not recorded.
/// </summary>
internal sealed class RecordingMount : IMount
{
    private readonly SimulatedMount _inner;
    private readonly List<(double Ra, double Dec)> _slews = new();
    private int _halts;

    public RecordingMount(SimulatedMount inner) => _inner = inner;

    public IReadOnlyList<(double Ra, double Dec)> Slews
    {
        get
        {
            lock (_slews)
            {
                return _slews.ToArray();
            }
        }
    }

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
        lock (_slews)
        {
            _slews.Add((raDegrees, decDegrees));
        }

        await _inner.SlewToCoordinatesAsync(raDegrees, decDegrees, cancellationToken).ConfigureAwait(false);
    }

    public Task<PierSide> GetSideOfPierAsync(CancellationToken cancellationToken = default) =>
        _inner.GetSideOfPierAsync(cancellationToken);

    public Task<GeodeticLocation> GetSiteLocationAsync(CancellationToken cancellationToken = default) =>
        _inner.GetSiteLocationAsync(cancellationToken);

    public Task SetSiteLocationAsync(GeodeticLocation site, CancellationToken cancellationToken = default) =>
        _inner.SetSiteLocationAsync(site, cancellationToken);

    public int Halts => Volatile.Read(ref _halts);

    public Task HaltAsync(CancellationToken cancellationToken = default)
    {
        Interlocked.Increment(ref _halts);
        return _inner.HaltAsync(cancellationToken);
    }

    public void Dispose() => _inner.Dispose();
}

/// <summary>
/// A camera whose frames are a few bytes on disk. The solvers used with it
/// never read them, but the files are real so that the engine's deleting and
/// keeping of frames (D26) can be observed. Each frame gets its own path, so a
/// test can tell which frame was solved.
/// </summary>
internal sealed class StubCamera : ICamera
{
    private readonly List<(DateTime StartUtc, TimeSpan Duration, CaptureContext? Context, string Path)> _frames = new();

    private readonly TimeProvider _time;

    public StubCamera(TimeProvider time, string? workingDirectory = null)
    {
        _time = time;
        WorkingDirectory = workingDirectory ?? Path.Combine(Path.GetTempPath(), $"fpa-stub-{Guid.NewGuid():N}");
        Directory.CreateDirectory(WorkingDirectory);
    }

    public string WorkingDirectory { get; }

    public string Name => "Stub Camera";

    public int SensorWidthPixels => 4000;

    public int SensorHeightPixels => 3000;

    public double PixelSizeMicrons => 3.76;

    public bool IsConnected { get; private set; }

    public IReadOnlyList<(DateTime StartUtc, TimeSpan Duration, CaptureContext? Context, string Path)> Frames
    {
        get
        {
            lock (_frames)
            {
                return _frames.ToArray();
            }
        }
    }

    public int Exposures => Frames.Count;

    public CaptureContext? LastContext => Frames.LastOrDefault().Context;

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

    /// <summary>The cable pulled: gone without being told to go.</summary>
    public void SimulateDisconnection() => IsConnected = false;

    public async Task<CapturedImage> ExposeAsync(
        TimeSpan duration, CaptureContext? context = null, CancellationToken cancellationToken = default)
    {
        if (!IsConnected)
        {
            throw new InvalidOperationException("The stub camera is not connected.");
        }

        DateTime start = _time.GetUtcNow().UtcDateTime;
        await Task.Delay(duration, _time, cancellationToken).ConfigureAwait(false);

        string path;
        lock (_frames)
        {
            path = System.IO.Path.Combine(WorkingDirectory, $"stub-{_frames.Count + 1:D5}.fits");
            _frames.Add((start, duration, context, path));
        }

        await File.WriteAllTextAsync(path, "SIMPLE  =                    T", cancellationToken).ConfigureAwait(false);

        return new CapturedImage(path, start + duration / 2, duration);
    }

    public void Dispose() => IsConnected = false;
}

/// <summary>
/// Reports exactly where the telescope is pointing, converted back to sky
/// coordinates through the same atmosphere the session will use to convert
/// them forward again. The round trip is therefore exact, which makes any
/// difference in a commanded coordinate attributable to the engine.
///
/// Also counts how many solves run at once, since D26 promises never more than
/// one.
/// </summary>
internal sealed class PerfectSolver : ISolver
{
    private readonly SimulatedMount _mount;
    private readonly ObserverSite _observer;
    private int _running;
    private int _calls;
    private int _mostAtOnce;

    public PerfectSolver(SimulatedMount mount, GeodeticLocation site, TimeSpan? delay = null)
    {
        _mount = mount;
        _observer = new ObserverSite(site.LatitudeDegrees, site.LongitudeDegrees, site.HeightMeters);
        Delay = delay ?? TimeSpan.Zero;
    }

    public string Name => "Perfect (test)";

    public TimeSpan Delay { get; set; }

    public int Calls => Volatile.Read(ref _calls);

    public int MostAtOnce => Volatile.Read(ref _mostAtOnce);

    public List<PlateSolveRequest> Requests { get; } = new();

    public async Task<PlateSolveResult> SolveAsync(PlateSolveRequest request, CancellationToken cancellationToken = default)
    {
        Interlocked.Increment(ref _calls);
        int now = Interlocked.Increment(ref _running);
        int seen;
        while ((seen = Volatile.Read(ref _mostAtOnce)) < now &&
               Interlocked.CompareExchange(ref _mostAtOnce, now, seen) != seen)
        {
        }

        lock (Requests)
        {
            Requests.Add(request);
        }

        try
        {
            if (Delay > TimeSpan.Zero)
            {
                await Task.Delay(Delay, _mount.Time, cancellationToken).ConfigureAwait(false);
            }

            DateTime utc = _mount.Time.GetUtcNow().UtcDateTime;
            HorizontalCoordinates pointing = _mount.PhysicalPointingAt(utc);
            (double ra, double dec) = TopocentricConverter.FromAltAz(pointing, utc, _observer, AtmosphericConditions.Standard);

            return PlateSolveResult.Succeeded(new PlateSolveSolution(
                ra, dec,
                PixelScaleArcsecPerPixel: 2.0,
                RotationDegrees: 0.0,
                Cd1_1: -2.0 / 3600.0, Cd1_2: 0.0, Cd2_1: 0.0, Cd2_2: 2.0 / 3600.0,
                MatchedStarCount: 40,
                SolveDuration: TimeSpan.Zero));
        }
        finally
        {
            Interlocked.Decrement(ref _running);
        }
    }
}

/// <summary>
/// Frames for stub cameras that exist to test something other than frames. The
/// camera now runs continuously once connected (D26), so a stub that refused
/// to expose would read as a camera that had died.
/// </summary>
internal static class StubFrames
{
    /// <summary>Short, so a command waiting for the frame in progress (D22, D23) does not wait long.</summary>
    public static readonly TimeSpan Exposure = TimeSpan.FromMilliseconds(10);

    public static async Task<CapturedImage> ExposeAsync(TimeSpan duration, TimeProvider time, CancellationToken cancellationToken)
    {
        await Task.Delay(duration, time, cancellationToken).ConfigureAwait(false);
        return new CapturedImage("never-written.fits", time.GetUtcNow().UtcDateTime, duration);
    }
}

/// <summary>A solver that never matches anything, for the failure path.</summary>
internal sealed class HopelessSolver : ISolver
{
    private int _calls;

    public string Name => "Hopeless (test)";

    public int Calls => Volatile.Read(ref _calls);

    public Task<PlateSolveResult> SolveAsync(PlateSolveRequest request, CancellationToken cancellationToken = default)
    {
        Interlocked.Increment(ref _calls);
        return Task.FromResult(PlateSolveResult.Failed(
            PlateSolveFailureReason.NoStarsDetected, "No stars were detected in the image."));
    }
}

/// <summary>
/// A session over the real simulated mount mechanics -- injected misalignment
/// and cone error included -- with a stub camera and a noiseless solver, and
/// with D26's delays shortened so a sequence runs in well under a second. What
/// this does not test is accuracy through a real solver; the exit-criterion
/// tests do that.
/// </summary>
internal sealed class SessionHarness : IDisposable
{
    public const double Latitude = 45.0;

    public static readonly GeodeticLocation Site = new(Latitude, 15.0, 200.0);

    public static readonly ObserverSite Observer = new(Latitude, 15.0, 200.0);

    public static readonly MountMisalignment Injected = new(30.0, -25.0);

    private readonly IDisposable _subscription;

    /// <param name="initialRotationDegrees">
    /// Where the simulated mount starts. Far enough east by default for a 60°
    /// sweep to be anchored where it stands (D18 as revised).
    /// </param>
    /// <param name="withMount">False for a session that never learns the mount exists (D10).</param>
    public SessionHarness(
        double initialRotationDegrees = -70.0,
        bool withMount = true,
        bool canSlew = true,
        bool tracking = true,
        TimeSpan? slewDuration = null,
        ISolver? solver = null,
        TimeSpan? settleDelay = null,
        TimeSpan? solveInterval = null,
        SimulatedSiteWrites siteWrites = SimulatedSiteWrites.Stored,
        bool canStopTracking = true)
    {
        Clock = new TestClock();
        Simulated = new SimulatedMount(new SimulatedMountOptions(
            Site,
            Injected,
            ConeErrorArcminutes: 20.0,
            ConePhaseDegrees: 35.0,
            Tracking: tracking,
            SlewDuration: slewDuration ?? TimeSpan.Zero,
            CanSlewAsync: canSlew,
            InitialMechanicalRotationDegrees: initialRotationDegrees,
            SiteWrites: siteWrites,
            CanStopTracking: canStopTracking), Clock);

        Mount = new RecordingMount(Simulated);
        Camera = new StubCamera(Clock);
        Perfect = new PerfectSolver(Simulated, Site);
        Solver = solver ?? Perfect;

        FailedSolvesDirectory = Path.Combine(Path.GetTempPath(), $"fpa-failed-{Guid.NewGuid():N}");

        var options = new AlignmentSessionOptions(
            CaptureCount: 5,
            SweepDegrees: 60.0,
            ExposureDuration: TimeSpan.FromMilliseconds(20),
            ExpectedSolveNoiseArcseconds: 3.0,
            SettleDelay: settleDelay ?? TimeSpan.FromMilliseconds(30),
            SolveInterval: solveInterval ?? TimeSpan.FromMilliseconds(60),
            FailedSolvesDirectory: FailedSolvesDirectory);

        Session = new AlignmentSession(Camera, withMount ? Mount : null, Solver, Clock, options);

        Recorder = new Recorder(Clock);
        _subscription = Session.Events.Subscribe(Recorder);
    }

    /// <summary>The fake clock the mount, camera, solver and engine all run on (D37).</summary>
    public TestClock Clock { get; }

    public SimulatedMount Simulated { get; }

    public RecordingMount Mount { get; }

    public StubCamera Camera { get; }

    public PerfectSolver Perfect { get; }

    public ISolver Solver { get; }

    public AlignmentSession Session { get; }

    public Recorder Recorder { get; }

    public string FailedSolvesDirectory { get; }

    public async Task ConnectAsync(bool mount = true)
    {
        await Session.SendAsync(new ConnectDeviceCommand(
            DeviceKind.Camera, AlignmentSession.AttachedProviderName, "camera"));
        if (mount)
        {
            await Session.SendAsync(new ConnectDeviceCommand(
                DeviceKind.Mount, AlignmentSession.AttachedProviderName, "mount"));
        }
    }

    public Task ConfirmSiteAsync() => Session
        .SendAsync(new ConfigureSiteCommand(Site.LatitudeDegrees, Site.LongitudeDegrees, Site.HeightMeters))
        .AsTask();

    public Task StartAsync(int points = 5, double sweep = 60.0) => Session
        .SendAsync(new StartSessionCommand(new SessionConfiguration(points, sweep)))
        .AsTask();

    public async Task ReadyAsync(bool mount = true)
    {
        await ConnectAsync(mount);
        await ConfirmSiteAsync();
        await StartAsync();
    }

    /// <summary>What the UI's status timer does, as often as a test needs it.</summary>
    public Task PollAsync() => Session.SendAsync(new RefreshMountStatusCommand()).AsTask();

    public Task<PointCapturedEvent> WaitForSampleAsync(int index, bool poll = true) =>
        Recorder.WaitForAsync<PointCapturedEvent>(
            e => e.Point.Index == index,
            fromIndex: Recorder.IndexOfLast<SequenceRestartedEvent>() + 1,
            poll: poll ? PollAsync : null);

    /// <summary>
    /// Moves the simulated mount directly, as a hand controller or another
    /// program would: the engine did not ask for it and is not told.
    /// </summary>
    public async Task MoveFromOutsideAsync(double mechanicalRotationDegrees, double? mechanicalDeclinationDegrees = null)
    {
        if (!Simulated.IsConnected)
        {
            await Simulated.ConnectAsync();
        }

        double declination = mechanicalDeclinationDegrees ?? MechanicalDeclinationNow();
        (double ra, double dec) = TargetSelection.ResolveCommand(
            Site, mechanicalRotationDegrees, declination, Clock.UtcNow);

        // A slew that takes time takes it on the fake clock, so the clock is
        // moved on until it arrives.
        Task slew = Simulated.SlewToCoordinatesAsync(ra, dec);
        await Clock.WaitUntilAsync(() => slew.IsCompleted, () => " The slew never arrived.");
        await slew;
    }

    /// <summary>The mount's own mechanical rotation, as the engine measures spacing by it (D27).</summary>
    public double MountRotationNow()
    {
        MountPosition position = Simulated.GetPositionAsync().GetAwaiter().GetResult();
        return TargetSelection.MechanicalRotationOf(Site, position.RaDegrees, position.DecDegrees, Clock.UtcNow);
    }

    public double MechanicalDeclinationNow()
    {
        var (_, declination) = MountMechanics.Decompose(
            Latitude, ToHorizontal(Simulated.GetPositionAsync().GetAwaiter().GetResult()));
        return declination;
    }

    /// <summary>
    /// The mechanical declination a commanded sky position corresponds to --
    /// computed the same geometric way the engine does, since it has to invert
    /// what the mount will do rather than model the atmosphere better than the
    /// mount does.
    /// </summary>
    public double MechanicalDeclinationOf(double raDegrees, double decDegrees)
    {
        HorizontalCoordinates pointing = TopocentricConverter.ToAltAz(
            raDegrees, decDegrees, Clock.UtcNow, Observer, AtmosphericConditions.Vacuum);

        var (_, declination) = MountMechanics.Decompose(Latitude, pointing);
        return declination;
    }

    private HorizontalCoordinates ToHorizontal(MountPosition position) =>
        TopocentricConverter.ToAltAz(
            position.RaDegrees, position.DecDegrees, Clock.UtcNow, Observer, AtmosphericConditions.Vacuum);

    public void Dispose()
    {
        _subscription.Dispose();
        Session.Dispose();

        foreach (string directory in new[] { FailedSolvesDirectory, Camera.WorkingDirectory })
        {
            try
            {
                Directory.Delete(directory, recursive: true);
            }
            catch (DirectoryNotFoundException)
            {
            }
        }
    }
}
