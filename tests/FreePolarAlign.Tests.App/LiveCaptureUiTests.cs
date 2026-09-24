using FreePolarAlign.App.Services;
using FreePolarAlign.App.ViewModels;
using FreePolarAlign.Core.Engine;
using FreePolarAlign.Imaging.Fits;
using FreePolarAlign.Session;
using Xunit;

namespace FreePolarAlign.Tests.App;

/// <summary>
/// The window's side of live capture (Phase 4d): which controls work when, the
/// preview keeping up with a camera that never stops, and saving a frame whose
/// file is about to be deleted.
///
/// The rules pinned here each reverse an older one. Exposure, gain and readout
/// were locked during a sequence, and D22 and D25 as revised unlock them. A
/// mount was required to start, and D10 as revised makes it optional. The
/// preview re-read the frame's file on every brightness change, and D26 deletes
/// that file as soon as a newer frame exists.
/// </summary>
public sealed class LiveCaptureUiTests : IDisposable
{
    private readonly string _directory =
        Path.Combine(Path.GetTempPath(), "fpa-live-ui-tests", Guid.NewGuid().ToString("N"));

    public LiveCaptureUiTests() => Directory.CreateDirectory(_directory);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private sealed class PublishingEngine : IAlignmentEngine
    {
        private readonly List<IObserver<EngineEvent>> _observers = new();

        public List<EngineCommand> Commands { get; } = new();

        public IObservable<EngineEvent> Events => new Subscribable(_observers);

        public ValueTask SendAsync(EngineCommand command, CancellationToken cancellationToken = default)
        {
            Commands.Add(command);
            return ValueTask.CompletedTask;
        }

        public void Publish(params EngineEvent[] events)
        {
            foreach (EngineEvent engineEvent in events)
            {
                foreach (IObserver<EngineEvent> observer in _observers.ToArray())
                {
                    observer.OnNext(engineEvent);
                }
            }
        }

        private sealed class Subscribable : IObservable<EngineEvent>
        {
            private readonly List<IObserver<EngineEvent>> _observers;

            public Subscribable(List<IObserver<EngineEvent>> observers) => _observers = observers;

            public IDisposable Subscribe(IObserver<EngineEvent> observer)
            {
                _observers.Add(observer);
                return new Unsubscriber(() => _observers.Remove(observer));
            }

            private sealed class Unsubscriber : IDisposable
            {
                private readonly Action _dispose;

                public Unsubscriber(Action dispose) => _dispose = dispose;

                public void Dispose() => _dispose();
            }
        }
    }

    /// <summary>Hands the view model a memory stream, and keeps what was written after it is closed.</summary>
    private sealed class MemorySaveTarget : IFrameSaveTarget
    {
        public List<string> SuggestedNames { get; } = new();

        public byte[]? Written { get; private set; }

        public Func<Stream?>? Override { get; init; }

        public Task<Stream?> OpenAsync(string suggestedFileName)
        {
            SuggestedNames.Add(suggestedFileName);
            if (Override is not null)
            {
                return Task.FromResult(Override());
            }

            var stream = new CapturingStream(bytes => Written = bytes);
            return Task.FromResult<Stream?>(stream);
        }

        private sealed class CapturingStream : MemoryStream
        {
            private readonly Action<byte[]> _onClose;

            public CapturingStream(Action<byte[]> onClose) => _onClose = onClose;

            protected override void Dispose(bool disposing)
            {
                if (disposing)
                {
                    _onClose(ToArray());
                }

                base.Dispose(disposing);
            }
        }
    }

    /// <summary>
    /// Records which frames were rendered, and at what brightness. Returns no
    /// bitmap: which frame is shown, and which are dropped, is decided before
    /// the bitmap, and building one needs a renderer these tests do not have.
    /// </summary>
    private sealed class RecordingRenderer
    {
        private readonly object _gate = new();

        public List<(double FirstPixel, double Target)> Calls { get; } = new();

        /// <summary>When set, render number <see cref="HoldAt"/> waits on it, so frames can pile up behind it.</summary>
        public ManualResetEventSlim? Hold { get; init; }

        public int HoldAt { get; init; }

        public int Count
        {
            get
            {
                lock (_gate)
                {
                    return Calls.Count;
                }
            }
        }

        public FramePreview Render(FitsImage image, double target)
        {
            bool held;
            lock (_gate)
            {
                held = Calls.Count == HoldAt;
                Calls.Add((image.Pixels[0, 0], target));
            }

            if (held)
            {
                Hold?.Wait(TimeSpan.FromSeconds(10));
            }

            return new FramePreview(null, null);
        }
    }

    private static readonly DeviceConnectedEvent CameraConnected = new(
        DeviceKind.Camera, "ZWO", "ZWO ASI290MM Mini", "ZWO ASI290MM Mini", "ZWO SDK",
        new CameraDescription(
            2.9, 1936, 1096,
            new[] { new CameraReadoutModeDescription(0, "RAW8", 8), new CameraReadoutModeDescription(1, "RAW16", 16) },
            ReadoutModeIndex: 1,
            UniqueId: "1A2B3C4D5E6F7081",
            Gain: new CameraGainDescription(25, 150, 0, 600)));

    private static readonly SiteConfiguredEvent SiteConfirmed = new(44.43, 26.10, 85.0);

    private static readonly SessionStartedEvent Started =
        new(new SessionConfiguration(6, 70.0), SequenceMode.Unconnected);

    private static (MainWindowViewModel ViewModel, PublishingEngine Engine) Build(
        IFrameSaveTarget? saveTarget = null,
        RecordingRenderer? renderer = null)
    {
        var engine = new PublishingEngine();
        renderer ??= new RecordingRenderer();

        var viewModel = new MainWindowViewModel(
            engine, catalog: null, settingsStore: null, settings: null, log: null, warnings: null,
            new SessionConfiguration(6, 70.0),
            saveTarget: saveTarget,
            renderPreview: renderer.Render);

        return (viewModel, engine);
    }

    /// <summary>
    /// A real FITS file whose first pixel identifies it, so a test can tell
    /// which frame a render was for.
    /// </summary>
    private string WriteFrame(int marker)
    {
        var pixels = new double[4, 4];
        pixels[0, 0] = marker;
        pixels[1, 1] = 1000.0;

        string path = Path.Combine(_directory, $"frame-{marker:D3}.fits");
        FitsFile.Write(path, FitsImage.ForCapturedFrame(4, 4, pixels, bitsPerPixel: 16));
        return path;
    }

    private static FrameCapturedEvent Frame(string path, int second = 0) =>
        new(path, new DateTime(2026, 9, 24, 21, 30, second, 250, DateTimeKind.Utc), TimeSpan.FromSeconds(0.5));

    // ---- Settings during a sequence ----

    /// <summary>
    /// Exposure, gain and readout can all be changed mid-sequence: each changes
    /// how noisy a solved position is, not where it is (D22, D25 as revised).
    /// Each used to be disabled here.
    /// </summary>
    [Fact]
    public void DuringASequence_ExposureGainAndReadoutAreAllEditable()
    {
        var (viewModel, engine) = Build();
        engine.Publish(CameraConnected, SiteConfirmed, Started);

        Assert.True(viewModel.IsSessionActive);
        Assert.True(viewModel.IsExposureEditable);
        Assert.True(viewModel.IsGainEditable);
        Assert.True(viewModel.IsReadoutModeEditable);

        viewModel.GainPercent = 40m;
        viewModel.SelectedReadoutMode = viewModel.ReadoutModes[0];

        Assert.Equal(40, Assert.Single(engine.Commands.OfType<SetCameraGainCommand>()).Percent);
        Assert.Equal(0, Assert.Single(engine.Commands.OfType<SetReadoutModeCommand>()).Index);
    }

    /// <summary>
    /// And none of them while the driver's window is open (D23). The engine is
    /// blocked behind the window, so a change would not fail: it would queue,
    /// and land afterwards on a camera that had changed underneath it. Refused
    /// in the setters too, not only by the disabled controls.
    /// </summary>
    [Fact]
    public void WhileTheDriverWindowIsOpen_NoneOfThemIsEditable()
    {
        var (viewModel, engine) = Build();
        engine.Publish(CameraConnected, SiteConfirmed, Started, new CameraSetupDialogChangedEvent(IsOpen: true));

        Assert.False(viewModel.IsExposureEditable);
        Assert.False(viewModel.IsGainEditable);
        Assert.False(viewModel.IsReadoutModeEditable);

        viewModel.SelectedExposure = ExposureOption.All[0];
        viewModel.GainPercent = 40m;
        viewModel.SelectedReadoutMode = viewModel.ReadoutModes[0];

        Assert.Empty(engine.Commands);
    }

    /// <summary>Gain and readout belong to a camera, so with none connected there is nothing to set.</summary>
    [Fact]
    public void WithNoCamera_GainAndReadoutAreNotEditable_ButTheExposureIs()
    {
        var (viewModel, _) = Build();

        Assert.False(viewModel.IsGainEditable);
        Assert.False(viewModel.IsReadoutModeEditable);
        Assert.True(viewModel.IsExposureEditable);
    }

    // ---- Starting and sampling ----

    /// <summary>
    /// A mount is optional (D10 as revised): without one, positions come from
    /// blind solves. Start used to demand both devices.
    /// </summary>
    [Fact]
    public void StartNeedsACameraAndASite_ButNoMount()
    {
        var (viewModel, engine) = Build();
        Assert.False(viewModel.StartCommand.CanExecute(null));

        engine.Publish(CameraConnected);
        Assert.False(viewModel.StartCommand.CanExecute(null));

        engine.Publish(SiteConfirmed);
        Assert.False(viewModel.IsMountConnected);
        Assert.True(viewModel.StartCommand.CanExecute(null));
    }

    /// <summary>
    /// Record sample forces one (D26), which only means something while a
    /// sequence is collecting them.
    /// </summary>
    [Fact]
    public void RecordSample_IsAvailableOnlyDuringASequence()
    {
        var (viewModel, engine) = Build();
        engine.Publish(CameraConnected, SiteConfirmed);
        Assert.False(viewModel.RecordSampleCommand.CanExecute(null));

        engine.Publish(Started);
        Assert.True(viewModel.RecordSampleCommand.CanExecute(null));
        viewModel.RecordSampleCommand.Execute(null);
        Assert.Single(engine.Commands.OfType<RecordSampleCommand>());

        engine.Publish(new SessionCompletedEvent());
        Assert.False(viewModel.RecordSampleCommand.CanExecute(null));
    }

    /// <summary>
    /// A proposal that needs no slew offers none. The telescope is either in
    /// place already or moved by hand, and a slew button would present a
    /// movement that is not going to happen (D18).
    /// </summary>
    [Fact]
    public void OnlyAProposalThatNeedsASlew_OffersOne()
    {
        var (viewModel, engine) = Build();
        engine.Publish(CameraConnected, SiteConfirmed, Started);

        engine.Publish(new SlewProposedEvent(1, 6, 150.0, 60.0, -35.0, 50.0, RequiresMotion: false, "Set declination to 60°."));
        Assert.False(viewModel.ConfirmProposalCommand.CanExecute(null));
        Assert.False(viewModel.ProposalRequiresMotion);
        Assert.True(viewModel.HasGuidance);

        engine.Publish(new SlewProposedEvent(2, 6, 165.0, 60.0, -21.0, 55.0, RequiresMotion: true, "Slew 14° west."));
        Assert.True(viewModel.ConfirmProposalCommand.CanExecute(null));
        viewModel.ConfirmProposalCommand.Execute(null);
        Assert.Single(engine.Commands.OfType<ConfirmSlewCommand>());
    }

    // ---- The live preview ----

    /// <summary>
    /// A burst of frames arriving while one is loading: only the newest is
    /// loaded next, and the ones between are never read at all. Loading every
    /// frame in turn would leave the picture further behind the camera with
    /// each one.
    /// </summary>
    [Fact]
    public async Task ABurstOfFrames_LoadsOnlyTheFirstAndTheNewest()
    {
        using var hold = new ManualResetEventSlim(false);
        var renderer = new RecordingRenderer { Hold = hold };
        var (viewModel, engine) = Build(renderer: renderer);

        engine.Publish(Frame(WriteFrame(1)));
        SpinUntil(() => renderer.Count == 1);

        for (int marker = 2; marker <= 6; marker++)
        {
            engine.Publish(Frame(WriteFrame(marker)));
        }

        hold.Set();
        await viewModel.WhenPreviewIdleAsync();

        Assert.Equal(new[] { 1.0, 6.0 }, renderer.Calls.Select(c => c.FirstPixel).ToArray());
        Assert.EndsWith("frame-006.fits", viewModel.DisplayedFrame!.SourcePath, StringComparison.Ordinal);
    }

    /// <summary>
    /// A frame whose file has gone by the time its turn comes is skipped without
    /// a word: the engine deletes superseded frames (D26), so it has simply been
    /// overtaken. The picture already showing stays, and no problem is shown.
    /// </summary>
    [Fact]
    public async Task AFrameWhoseFileIsGone_IsSkippedSilently()
    {
        var renderer = new RecordingRenderer();
        var (viewModel, engine) = Build(renderer: renderer);

        engine.Publish(Frame(WriteFrame(1)));
        await viewModel.WhenPreviewIdleAsync();

        string gone = WriteFrame(2);
        File.Delete(gone);
        engine.Publish(Frame(gone));
        await viewModel.WhenPreviewIdleAsync();

        Assert.Single(renderer.Calls);
        Assert.EndsWith("frame-001.fits", viewModel.DisplayedFrame!.SourcePath, StringComparison.Ordinal);
        Assert.False(viewModel.HasFramePreviewProblem);
    }

    /// <summary>
    /// The brightness control re-stretches the frame in memory. It used to
    /// re-read the file, which D26 deletes once a newer frame exists, so dragging
    /// the slider would have failed on every frame but the newest.
    /// </summary>
    [Fact]
    public async Task Brightness_RestretchesTheFrameInMemory_AfterItsFileHasGone()
    {
        var renderer = new RecordingRenderer();
        var (viewModel, engine) = Build(renderer: renderer);

        string path = WriteFrame(7);
        engine.Publish(Frame(path));
        await viewModel.WhenPreviewIdleAsync();
        File.Delete(path);

        viewModel.StretchTarget = 0.5;
        await viewModel.WhenPreviewIdleAsync();

        Assert.Equal((7.0, 0.5), renderer.Calls[^1]);
        Assert.False(viewModel.HasFramePreviewProblem);
    }

    /// <summary>
    /// Dragging the slider while a newer frame is waiting does not displace
    /// that frame: it is rendered at the new brightness anyway, and dropping it
    /// would hold the picture back by a whole exposure.
    /// </summary>
    [Fact]
    public async Task ARestretch_DoesNotDisplaceAWaitingFrame()
    {
        using var hold = new ManualResetEventSlim(false);
        var renderer = new RecordingRenderer { Hold = hold, HoldAt = 1 };
        var (viewModel, engine) = Build(renderer: renderer);

        // One frame on screen, so there is something to re-stretch.
        engine.Publish(Frame(WriteFrame(1)));
        await viewModel.WhenPreviewIdleAsync();

        engine.Publish(Frame(WriteFrame(2)));
        SpinUntil(() => renderer.Count == 2);
        engine.Publish(Frame(WriteFrame(3)));
        viewModel.StretchTarget = 0.5;

        hold.Set();
        await viewModel.WhenPreviewIdleAsync();

        Assert.Equal((3.0, 0.5), renderer.Calls[^1]);
        Assert.Equal(3, renderer.Calls.Count);
    }

    // ---- Save frame ----

    [Fact]
    public void SaveFrame_NeedsAFrameOnScreen()
    {
        var (viewModel, _) = Build(saveTarget: new MemorySaveTarget());

        Assert.False(viewModel.SaveFrameCommand.CanExecute(null));
    }

    /// <summary>
    /// The saved file is the frame that was on screen, byte for byte, even
    /// though its own file was deleted before Save was pressed. The engine
    /// deletes a frame once a newer one exists (D26), so a save that copied the
    /// file would fail for any frame not the very newest.
    /// </summary>
    [Fact]
    public async Task SaveFrame_WritesTheDisplayedFramesBytes_AfterItsFileIsDeleted()
    {
        var target = new MemorySaveTarget();
        var (viewModel, engine) = Build(saveTarget: target);

        string path = WriteFrame(3);
        byte[] original = File.ReadAllBytes(path);
        engine.Publish(Frame(path, second: 7));
        await viewModel.WhenPreviewIdleAsync();
        File.Delete(path);

        Assert.True(viewModel.SaveFrameCommand.CanExecute(null));
        viewModel.SaveFrameCommand.Execute(null);

        Assert.Equal(original, target.Written);
        Assert.Equal("frame_20260924T213007.250Z.fits", Assert.Single(target.SuggestedNames));
        Assert.False(viewModel.HasSaveFrameProblem);
    }

    /// <summary>A save that fails says so rather than throwing out of a button handler.</summary>
    [Fact]
    public async Task AFailedSave_IsShownNotThrown()
    {
        var target = new MemorySaveTarget { Override = () => throw new IOException("The disk is full.") };
        var (viewModel, engine) = Build(saveTarget: target);
        engine.Publish(Frame(WriteFrame(4)));
        await viewModel.WhenPreviewIdleAsync();

        viewModel.SaveFrameCommand.Execute(null);

        Assert.True(viewModel.HasSaveFrameProblem);
        Assert.Contains("The disk is full.", viewModel.SaveFrameProblem, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CancellingTheSaveDialog_WritesNothingAndSaysNothing()
    {
        var target = new MemorySaveTarget { Override = () => null };
        var (viewModel, engine) = Build(saveTarget: target);
        engine.Publish(Frame(WriteFrame(5)));
        await viewModel.WhenPreviewIdleAsync();

        viewModel.SaveFrameCommand.Execute(null);

        Assert.Null(target.Written);
        Assert.False(viewModel.HasSaveFrameProblem);
    }

    /// <summary>
    /// Save frame and Record sample are refused while the driver's window is
    /// open, like every other command (D23), in a state where both would
    /// otherwise be available.
    /// </summary>
    [Fact]
    public async Task WhileTheDriverWindowIsOpen_NeitherSaveNorRecordSampleWillRun()
    {
        var (viewModel, engine) = Build(saveTarget: new MemorySaveTarget());
        engine.Publish(CameraConnected, SiteConfirmed, Started, Frame(WriteFrame(8)));
        await viewModel.WhenPreviewIdleAsync();
        Assert.True(viewModel.SaveFrameCommand.CanExecute(null));
        Assert.True(viewModel.RecordSampleCommand.CanExecute(null));

        engine.Publish(new CameraSetupDialogChangedEvent(IsOpen: true));

        Assert.False(viewModel.SaveFrameCommand.CanExecute(null));
        Assert.False(viewModel.RecordSampleCommand.CanExecute(null));
    }

    private static void SpinUntil(Func<bool> condition)
    {
        Assert.True(SpinWait.SpinUntil(condition, TimeSpan.FromSeconds(10)), "timed out waiting");
    }
}
