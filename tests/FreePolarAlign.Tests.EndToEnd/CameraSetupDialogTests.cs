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
/// Opening the camera driver's own settings window.
///
/// It exists because this project deliberately models almost nothing about a
/// camera -- not gain, not offset, not cooling -- and on real hardware those
/// settings decide whether a frame is usable. A session was lost to a camera
/// left at a gain that put the sky at 57% of full well: the stars were there,
/// the software was right, and nothing in the application could change the one
/// number that mattered.
///
/// The behaviour worth pinning is not that a window appears -- no test can see
/// one -- but that the engine stops dead while it is up. The window changes the
/// device the next exposure comes from, so anything that ran alongside it would
/// be operating on a camera in an unknown state.
/// </summary>
public class CameraSetupDialogTests
{
    private static readonly GeodeticLocation Site = new(45.0, 15.0, 200.0);

    /// <summary>A camera whose settings window this test opens and closes by hand.</summary>
    private sealed class DialogCamera : ICamera
    {
        private readonly TaskCompletionSource _closed = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public DialogCamera(bool hasSetupDialog = true, bool blocks = false, Exception? failWith = null)
        {
            HasSetupDialog = hasSetupDialog;
            Blocks = blocks;
            FailWith = failWith;
        }

        public string Name => "Dialog Camera";

        public int SensorWidthPixels => 4000;

        public int SensorHeightPixels => 3000;

        public double PixelSizeMicrons => 3.76;

        public bool IsConnected { get; private set; }

        public bool HasSetupDialog { get; }

        private bool Blocks { get; }

        private Exception? FailWith { get; }

        /// <summary>How many times the driver was asked to show its window.</summary>
        public int DialogsShown { get; private set; }

        public IReadOnlyList<CameraReadoutMode> ReadoutModes => Array.Empty<CameraReadoutMode>();

        public int? ReadoutModeIndex => null;

        public Task SetReadoutModeAsync(int index, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException("This camera has no selectable readout modes.");

        public Task ShowSetupDialogAsync(CancellationToken cancellationToken = default)
        {
            DialogsShown++;

            if (FailWith is not null)
            {
                return Task.FromException(FailWith);
            }

            return Blocks ? _closed.Task : Task.CompletedTask;
        }

        /// <summary>Stands in for the user closing the window.</summary>
        public void CloseDialog() => _closed.TrySetResult();

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

    /// <summary>A camera with no settings window at all, which is every camera the simulator provides.</summary>
    private sealed class PlainCamera : ICamera
    {
        public string Name => "Plain Camera";

        public int SensorWidthPixels => 4000;

        public int SensorHeightPixels => 3000;

        public double PixelSizeMicrons => 3.76;

        public bool IsConnected { get; private set; }

        public IReadOnlyList<CameraReadoutMode> ReadoutModes => Array.Empty<CameraReadoutMode>();

        public int? ReadoutModeIndex => null;

        public Task SetReadoutModeAsync(int index, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException("This camera has no selectable readout modes.");

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

    private sealed class UnusedSolver : ISolver
    {
        public string Name => "Unused (test)";

        public Task<PlateSolveResult> SolveAsync(
            PlateSolveRequest request, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException("These tests never solve.");
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

        public Harness(ICamera camera)
        {
            Camera = camera;
            Mount = new SimulatedMount(new SimulatedMountOptions(Site, new MountMisalignment(30.0, -25.0)));
            Session = new AlignmentSession(
                camera, Mount, new UnusedSolver(), new AlignmentSessionOptions(CaptureCount: 5, SweepDegrees: 60.0));

            Recorder = new Recorder();
            _subscription = Session.Events.Subscribe(Recorder);
        }

        public ICamera Camera { get; }

        public SimulatedMount Mount { get; }

        public AlignmentSession Session { get; }

        public Recorder Recorder { get; }

        public async Task ConnectAsync()
        {
            await Session.SendAsync(new ConnectDeviceCommand(
                DeviceKind.Camera, AlignmentSession.AttachedProviderName, "camera"));
            await Session.SendAsync(new ConnectDeviceCommand(
                DeviceKind.Mount, AlignmentSession.AttachedProviderName, "mount"));
        }

        public async Task StartAsync()
        {
            await Session.SendAsync(new ConfigureSiteCommand(
                Site.LatitudeDegrees, Site.LongitudeDegrees, Site.HeightMeters));
            await Session.SendAsync(new StartSessionCommand(
                new SessionConfiguration(5, 60.0, TimeSpan.FromSeconds(1))));
        }

        public void Dispose()
        {
            _subscription.Dispose();
            Session.Dispose();
            Mount.Dispose();
        }
    }

    private static IEnumerable<bool> DialogStates(Recorder recorder) =>
        recorder.Events.OfType<CameraSetupDialogChangedEvent>().Select(e => e.IsOpen);

    private static string? LastRejection(Recorder recorder) =>
        recorder.Events.OfType<CommandRejectedEvent>().LastOrDefault()?.Reason;

    // ---- The happy path ----

    /// <summary>
    /// The window is shown, and the engine says so either side of it. Both
    /// events matter: the first is what lets a UI refuse everything, and the
    /// second is what lets it start accepting again.
    /// </summary>
    [Fact]
    public async Task OpeningTheDialog_ShowsItAndBracketsItWithEvents()
    {
        var camera = new DialogCamera();
        using var harness = new Harness(camera);
        await harness.ConnectAsync();

        await harness.Session.SendAsync(new OpenCameraSetupDialogCommand());

        Assert.Equal(1, camera.DialogsShown);
        Assert.Equal(new[] { true, false }, DialogStates(harness.Recorder));
    }

    /// <summary>
    /// The capability is reported at connect time, because it is what decides
    /// whether the UI offers the button at all.
    /// </summary>
    [Fact]
    public async Task ConnectingReportsWhetherTheDriverHasASettingsWindow()
    {
        using var withDialog = new Harness(new DialogCamera());
        await withDialog.ConnectAsync();

        using var without = new Harness(new PlainCamera());
        await without.ConnectAsync();

        Assert.True(Connected(withDialog).Camera!.HasSetupDialog);
        Assert.False(Connected(without).Camera!.HasSetupDialog);

        static DeviceConnectedEvent Connected(Harness harness) => harness.Recorder.Events
            .OfType<DeviceConnectedEvent>()
            .First(e => e.Kind == DeviceKind.Camera);
    }

    // ---- Nothing else runs while it is open ----

    /// <summary>
    /// The guarantee the whole feature rests on: while the window is up, the
    /// engine processes nothing else.
    ///
    /// It holds because the command gate is held for the duration, and it has to
    /// hold: the window changes gain, offset and readout, so a capture that
    /// straddled it would be taken half under the old settings and half under
    /// the new, with nothing in the frame to say which.
    /// </summary>
    [Fact]
    public async Task WhileTheDialogIsOpen_NoOtherCommandRuns()
    {
        var camera = new DialogCamera(blocks: true);
        using var harness = new Harness(camera);
        await harness.ConnectAsync();

        Task opening = harness.Session.SendAsync(new OpenCameraSetupDialogCommand()).AsTask();

        // The dialog is up: the open event has been published and the command
        // has not returned.
        await WaitUntil(() => DialogStates(harness.Recorder).Any());
        Assert.False(opening.IsCompleted);

        // Connecting the mount already reported its position once, so what is
        // being watched is whether a *further* report appears.
        int reportsBefore = harness.Recorder.Events.OfType<MountStatusEvent>().Count();

        Task blocked = harness.Session.SendAsync(new RefreshMountStatusCommand()).AsTask();

        // Long enough that it would have run had nothing been holding it. Without
        // this the assertion would pass on a command that simply had not started.
        await Task.Delay(100);

        Assert.False(blocked.IsCompleted);
        Assert.Equal(reportsBefore, harness.Recorder.Events.OfType<MountStatusEvent>().Count());

        camera.CloseDialog();

        await opening;
        await blocked;

        Assert.Equal(new[] { true, false }, DialogStates(harness.Recorder));
        Assert.Equal(reportsBefore + 1, harness.Recorder.Events.OfType<MountStatusEvent>().Count());
    }

    // ---- Refusals ----

    [Fact]
    public async Task WithNoCameraConnected_TheCommandIsRefused()
    {
        using var harness = new Harness(new DialogCamera());

        await harness.Session.SendAsync(new OpenCameraSetupDialogCommand());

        Assert.Empty(DialogStates(harness.Recorder));
        Assert.Contains("Connect a camera", LastRejection(harness.Recorder));
    }

    /// <summary>
    /// A camera with no settings window says so rather than appearing to open
    /// one. The simulator is in this position, and a button that silently did
    /// nothing would be read as a broken driver.
    /// </summary>
    [Fact]
    public async Task ACameraWithNoSettingsWindow_RefusesToOpenOne()
    {
        using var harness = new Harness(new PlainCamera());
        await harness.ConnectAsync();

        await harness.Session.SendAsync(new OpenCameraSetupDialogCommand());

        Assert.Empty(DialogStates(harness.Recorder));
        Assert.Contains("no driver settings window", LastRejection(harness.Recorder));
    }

    /// <summary>
    /// Refused mid-sequence, for the reason the readout mode is: the frames
    /// already captured were taken under different settings, and the fit weights
    /// every observation alike.
    /// </summary>
    [Fact]
    public async Task DuringASequence_TheCommandIsRefused()
    {
        var camera = new DialogCamera();
        using var harness = new Harness(camera);
        await harness.ConnectAsync();
        await harness.StartAsync();

        await harness.Session.SendAsync(new OpenCameraSetupDialogCommand());

        Assert.Equal(0, camera.DialogsShown);
        Assert.Empty(DialogStates(harness.Recorder));
        Assert.Contains("during a sequence", LastRejection(harness.Recorder));
    }

    /// <summary>
    /// A driver that throws still leaves the application usable. Without the
    /// closing event the UI would refuse every command for the rest of the
    /// session, waiting on a window that never opened.
    /// </summary>
    [Fact]
    public async Task ADriverThatFails_StillReportsTheDialogClosed()
    {
        var camera = new DialogCamera(failWith: new InvalidOperationException("COM says no"));
        using var harness = new Harness(camera);
        await harness.ConnectAsync();

        await harness.Session.SendAsync(new OpenCameraSetupDialogCommand());

        Assert.Equal(new[] { true, false }, DialogStates(harness.Recorder));
        Assert.Contains("COM says no", LastRejection(harness.Recorder));

        // And the engine still works afterwards.
        await harness.Session.SendAsync(new RefreshMountStatusCommand());
        Assert.NotEmpty(harness.Recorder.Events.OfType<MountStatusEvent>());
    }

    private static async Task WaitUntil(Func<bool> condition)
    {
        for (int attempt = 0; attempt < 500 && !condition(); attempt++)
        {
            await Task.Delay(10);
        }

        Assert.True(condition(), "the condition was never met");
    }
}
