using FreePolarAlign.Core.Alignment;
using FreePolarAlign.Core.Astrometry;
using FreePolarAlign.Core.Engine;
using FreePolarAlign.Devices;
using FreePolarAlign.Devices.Simulated;
using FreePolarAlign.Session;
using FreePolarAlign.Solving;
using Xunit;

namespace FreePolarAlign.Tests.EndToEnd;

/// <summary>
/// Gain, set through the engine as a percentage of the camera's own range.
///
/// It exists for native-SDK cameras. Through ASCOM the application cannot see or
/// set gain, and a camera left at a high one put the sky at 57% of full well on
/// the night a solve mattered most. The engine owns the mapping from percentage
/// to raw units, so the same command means the same thing on any camera.
/// </summary>
public class CameraGainTests
{
    private static readonly GeodeticLocation Site = new(45.0, 15.0, 200.0);

    /// <summary>A camera with a gain control, shaped like an ASI290: 0 to 600, clamping like the real SDK.</summary>
    private sealed class GainCamera : ICamera
    {
        private int _gain;

        public GainCamera(CameraGainRange? range = null, int initialGain = 150, int? hardwareCeiling = null)
        {
            GainRange = range ?? new CameraGainRange(0, 600);
            _gain = initialGain;
            HardwareCeiling = hardwareCeiling;
        }

        /// <summary>A value above which the camera silently refuses to go, whatever its range says.</summary>
        private int? HardwareCeiling { get; }

        public string Name => "Gain Camera";

        public int SensorWidthPixels => 1936;

        public int SensorHeightPixels => 1096;

        public double PixelSizeMicrons => 2.9;

        public bool IsConnected { get; private set; }

        public string? UniqueId => "1A2B3C4D5E6F7081";

        public CameraGainRange? GainRange { get; }

        public int? Gain => GainRange is null ? null : _gain;

        public int SetCalls { get; private set; }

        public IReadOnlyList<CameraReadoutMode> ReadoutModes => Array.Empty<CameraReadoutMode>();

        public int? ReadoutModeIndex => null;

        public Task SetReadoutModeAsync(int index, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task SetGainAsync(int value, CancellationToken cancellationToken = default)
        {
            SetCalls++;
            int clamped = GainRange!.Clamp(value);
            _gain = HardwareCeiling is { } ceiling ? Math.Min(clamped, ceiling) : clamped;
            return Task.CompletedTask;
        }

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

        public Task<CapturedImage> ExposeAsync(
            TimeSpan duration, CaptureContext? context = null, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException("These tests never expose.");

        public void Dispose()
        {
        }
    }

    /// <summary>An ASCOM-shaped camera: its gain is its driver's business, so it reports no range.</summary>
    private sealed class NoGainCamera : ICamera
    {
        public string Name => "ASCOM-ish Camera";

        public int SensorWidthPixels => 1936;

        public int SensorHeightPixels => 1096;

        public double PixelSizeMicrons => 2.9;

        public bool IsConnected { get; private set; }

        public IReadOnlyList<CameraReadoutMode> ReadoutModes => Array.Empty<CameraReadoutMode>();

        public int? ReadoutModeIndex => null;

        public Task SetReadoutModeAsync(int index, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

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

        public Task<CapturedImage> ExposeAsync(
            TimeSpan duration, CaptureContext? context = null, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public void Dispose()
        {
        }
    }

    private sealed class UnusedSolver : ISolver
    {
        public string Name => "Unused (test)";

        public Task<PlateSolveResult> SolveAsync(PlateSolveRequest request, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }

    private sealed class Recorder : IObserver<EngineEvent>
    {
        public List<EngineEvent> Events { get; } = new();

        public void OnCompleted()
        {
        }

        public void OnError(Exception error)
        {
        }

        public void OnNext(EngineEvent value) => Events.Add(value);
    }

    private sealed class Harness : IDisposable
    {
        private readonly IDisposable _subscription;
        private readonly SimulatedMount _mount;

        public Harness(ICamera camera)
        {
            _mount = new SimulatedMount(new SimulatedMountOptions(Site, new MountMisalignment(30.0, -25.0)));
            Session = new AlignmentSession(
                camera, _mount, new UnusedSolver(), new AlignmentSessionOptions(CaptureCount: 5, SweepDegrees: 60.0));
            _subscription = Session.Events.Subscribe(Recorder);
        }

        public AlignmentSession Session { get; }

        public Recorder Recorder { get; } = new();

        public async Task ConnectAsync(bool mountToo = false)
        {
            await Session.SendAsync(new ConnectDeviceCommand(DeviceKind.Camera, AlignmentSession.AttachedProviderName, "camera"));
            if (mountToo)
            {
                await Session.SendAsync(new ConnectDeviceCommand(DeviceKind.Mount, AlignmentSession.AttachedProviderName, "mount"));
            }
        }

        public CameraGainChangedEvent? LastGain => Recorder.Events.OfType<CameraGainChangedEvent>().LastOrDefault();

        public string? LastRejection => Recorder.Events.OfType<CommandRejectedEvent>().LastOrDefault()?.Reason;

        public void Dispose()
        {
            _subscription.Dispose();
            Session.Dispose();
            _mount.Dispose();
        }
    }

    // ---- Connecting ----

    /// <summary>
    /// The camera's identity and its gain are reported at connect, because the
    /// first keys the settings remembered for it and the second decides whether
    /// the UI offers a gain control at all.
    /// </summary>
    [Fact]
    public async Task ConnectingReportsTheCamerasSerialAndGain()
    {
        using var harness = new Harness(new GainCamera(initialGain: 150));
        await harness.ConnectAsync();

        CameraDescription camera = harness.Recorder.Events.OfType<DeviceConnectedEvent>().Single().Camera!;
        Assert.Equal("1A2B3C4D5E6F7081", camera.UniqueId);
        Assert.Equal(new CameraGainDescription(Percent: 25, Value: 150, Minimum: 0, Maximum: 600), camera.Gain);
    }

    [Fact]
    public async Task AnAscomShapedCameraReportsNoGain()
    {
        using var harness = new Harness(new NoGainCamera());
        await harness.ConnectAsync();

        Assert.Null(harness.Recorder.Events.OfType<DeviceConnectedEvent>().Single().Camera!.Gain);
    }

    // ---- Setting it ----

    [Fact]
    public async Task APercentageIsMappedOntoTheCamerasOwnRange()
    {
        var camera = new GainCamera();
        using var harness = new Harness(camera);
        await harness.ConnectAsync();

        await harness.Session.SendAsync(new SetCameraGainCommand(50));

        Assert.Equal(300, camera.Gain);
        Assert.Equal(new CameraGainDescription(50, 300, 0, 600), harness.LastGain!.Gain);
    }

    /// <summary>
    /// What is reported is what the camera took, read back -- not the request
    /// echoed. A camera that stops short of what was asked must show where it
    /// actually is, or every frame's header and the control both lie.
    /// </summary>
    [Fact]
    public async Task TheGainReportedIsWhatTheCameraTook_NotWhatWasAsked()
    {
        var camera = new GainCamera(hardwareCeiling: 480);
        using var harness = new Harness(camera);
        await harness.ConnectAsync();

        await harness.Session.SendAsync(new SetCameraGainCommand(100));

        Assert.Equal(480, harness.LastGain!.Gain.Value);
        Assert.Equal(80, harness.LastGain.Gain.Percent);
    }

    /// <summary>
    /// On a narrow range several percentages share a raw value. The one typed is
    /// the one shown, rather than a neighbour produced by converting back.
    /// </summary>
    [Fact]
    public async Task OnANarrowRange_TheTypedPercentageIsKept()
    {
        using var harness = new Harness(new GainCamera(new CameraGainRange(0, 10), initialGain: 0));
        await harness.ConnectAsync();

        await harness.Session.SendAsync(new SetCameraGainCommand(37));

        Assert.Equal(4, harness.LastGain!.Gain.Value);
        Assert.Equal(37, harness.LastGain.Gain.Percent);
    }

    // ---- Refusals ----

    [Fact]
    public async Task WithNoCameraConnected_GainIsRefused()
    {
        var camera = new GainCamera();
        using var harness = new Harness(camera);

        await harness.Session.SendAsync(new SetCameraGainCommand(50));

        Assert.Equal(0, camera.SetCalls);
        Assert.Contains("Connect a camera", harness.LastRejection);
    }

    /// <summary>
    /// An ASCOM camera's gain belongs to its driver's window (D23); the refusal
    /// says where to go instead.
    /// </summary>
    [Fact]
    public async Task ACameraWithoutGainControl_PointsAtItsDriverSettings()
    {
        using var harness = new Harness(new NoGainCamera());
        await harness.ConnectAsync();

        await harness.Session.SendAsync(new SetCameraGainCommand(50));

        Assert.Null(harness.LastGain);
        Assert.Contains("driver settings", harness.LastRejection);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(101)]
    public async Task APercentageOutsideTheControlIsRefused(int percent)
    {
        var camera = new GainCamera();
        using var harness = new Harness(camera);
        await harness.ConnectAsync();

        await harness.Session.SendAsync(new SetCameraGainCommand(percent));

        Assert.Equal(0, camera.SetCalls);
        Assert.Contains("0 to 100", harness.LastRejection);
    }

    /// <summary>
    /// Refused mid-sequence: gain changes the noise in every star position, and
    /// the fit weights every frame alike.
    /// </summary>
    [Fact]
    public async Task DuringASequence_GainIsRefused()
    {
        var camera = new GainCamera();
        using var harness = new Harness(camera);
        await harness.ConnectAsync(mountToo: true);
        await harness.Session.SendAsync(new ConfigureSiteCommand(Site.LatitudeDegrees, Site.LongitudeDegrees, Site.HeightMeters));
        await harness.Session.SendAsync(new StartSessionCommand(new SessionConfiguration(5, 60.0, TimeSpan.FromSeconds(1))));

        await harness.Session.SendAsync(new SetCameraGainCommand(80));

        Assert.Equal(0, camera.SetCalls);
        Assert.Contains("during a sequence", harness.LastRejection);
    }
}
