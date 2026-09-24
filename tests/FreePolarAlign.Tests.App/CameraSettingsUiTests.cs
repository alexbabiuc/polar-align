using FreePolarAlign.App.ViewModels;
using FreePolarAlign.Core.Engine;
using FreePolarAlign.Devices;
using FreePolarAlign.Session;
using Xunit;

namespace FreePolarAlign.Tests.App;

/// <summary>
/// Which camera-settings control is offered for which kind of camera, and what
/// is remembered per camera.
///
/// An ASCOM camera gets its driver's own window and no gain control: the gain
/// lives behind that window, and a second control fighting it over the same
/// value would leave the user unsure which won. A native-SDK camera has no such
/// window, and gets a gain control instead. Each is offered as soon as a camera
/// is picked, not only once it is connected -- the ASCOM window is for setting a
/// camera up before connecting.
///
/// The readout mode and the gain are remembered per camera because both mean
/// different things on different cameras: readout index 1 is a position in one
/// camera's list, and a gain chosen for one sensor over-exposes another.
/// </summary>
public class CameraSettingsUiTests
{
    private const string AscomId = "ASCOM.ASICamera2.Camera";
    private const string ZwoId = "ZWO ASI290MM Mini";

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

    private sealed class InMemorySettingsStore : ISettingsStore
    {
        public AppSettings Saved { get; private set; } = AppSettings.Empty;

        public string Location => "(in memory)";

        public SettingsLoadResult Load() => new(Saved, null);

        public string? Save(AppSettings settings)
        {
            Saved = settings;
            return null;
        }
    }

    /// <summary>Lists one ASCOM-shaped and one native-shaped camera, as the real providers describe them.</summary>
    private sealed class ListingProvider : IDeviceProvider
    {
        public ListingProvider(string name, DeviceDescriptor camera)
        {
            Name = name;
            Camera = camera;
        }

        public string Name { get; }

        private DeviceDescriptor Camera { get; }

        public string Version => "0";

        public IReadOnlyList<DeviceDescriptor> DiscoverDevices() => new[] { Camera };

        public ICamera OpenCamera(string deviceId) => throw new NotSupportedException();

        public IMount OpenMount(string deviceId) => throw new NotSupportedException();
    }

    private static readonly IReadOnlyList<CameraReadoutModeDescription> TwoModes = new[]
    {
        new CameraReadoutModeDescription(0, "RAW8", 8),
        new CameraReadoutModeDescription(1, "RAW16", 16),
    };

    private static (MainWindowViewModel ViewModel, PublishingEngine Engine, InMemorySettingsStore Store, DeviceCatalog Catalog) Build(
        AppSettings? settings = null)
    {
        var engine = new PublishingEngine();
        var store = new InMemorySettingsStore();
        DeviceCatalog catalog = DeviceCatalog.Create(new IDeviceProvider[]
        {
            new ListingProvider("ASCOM", new CameraDescriptor(AscomId, "ZWO ASI Camera (1)", AscomId, HasSetupDialog: true)),
            new ListingProvider("ZWO", new CameraDescriptor(ZwoId, ZwoId, "ZWO SDK", HasGainControl: true)),
        });

        var viewModel = new MainWindowViewModel(
            engine, catalog, store, settings, log: null, warnings: null,
            new SessionConfiguration(6, 70.0, TimeSpan.FromSeconds(2)));

        return (viewModel, engine, store, catalog);
    }

    private static DeviceOption Option(DeviceCatalog catalog, string provider) =>
        catalog.Cameras.Single(c => c.ProviderName == provider);

    private static DeviceConnectedEvent ZwoConnected(string serial = "1A2B3C4D5E6F7081", int readout = 1, int gainPercent = 25) => new(
        DeviceKind.Camera, "ZWO", ZwoId, ZwoId, "ZWO SDK",
        new CameraDescription(
            2.9, 1936, 1096, TwoModes, readout, HasSetupDialog: false, UniqueId: serial,
            Gain: new CameraGainDescription(gainPercent, gainPercent * 6, 0, 600)));

    private static DeviceConnectedEvent AscomConnected(int readout = 0) => new(
        DeviceKind.Camera, "ASCOM", AscomId, "ZWO ASI Camera (1)", AscomId,
        new CameraDescription(2.9, 1936, 1096, TwoModes, readout, HasSetupDialog: true));

    // ---- Which control, for which camera ----

    [Fact]
    public void PickingAnAscomCamera_OffersItsDriverWindow_AndNoGainControl()
    {
        var (viewModel, _, _, catalog) = Build();

        viewModel.SelectedCamera = Option(catalog, "ASCOM");

        Assert.True(viewModel.HasCameraSetupDialog);
        Assert.False(viewModel.ShowGainControl);
    }

    /// <summary>
    /// Before connecting, too: the window is where the camera is set up, and the
    /// command names the picked camera so the engine knows which driver to open.
    /// </summary>
    [Fact]
    public void TheAscomWindowCanBeOpenedBeforeConnecting_ForThePickedCamera()
    {
        var (viewModel, engine, _, catalog) = Build();
        viewModel.SelectedCamera = Option(catalog, "ASCOM");

        Assert.True(viewModel.OpenCameraSetupCommand.CanExecute(null));
        viewModel.OpenCameraSetupCommand.Execute(null);

        OpenCameraSetupDialogCommand sent = Assert.Single(engine.Commands.OfType<OpenCameraSetupDialogCommand>());
        Assert.Equal(("ASCOM", AscomId), (sent.ProviderName, sent.DeviceId));
    }

    /// <summary>
    /// A native camera gets the gain control as soon as it is picked, disabled
    /// until connecting tells it the camera's range, and no driver window --
    /// there is none to open.
    /// </summary>
    [Fact]
    public void PickingANativeCamera_ShowsGain_DisabledUntilConnected()
    {
        var (viewModel, _, _, catalog) = Build();

        viewModel.SelectedCamera = Option(catalog, "ZWO");

        Assert.False(viewModel.HasCameraSetupDialog);
        Assert.False(viewModel.OpenCameraSetupCommand.CanExecute(null));
        Assert.True(viewModel.ShowGainControl);
        Assert.False(viewModel.IsGainEditable);
        Assert.Null(viewModel.GainPercent);
    }

    [Fact]
    public void AConnectedNativeCamera_ShowsItsGainAsAPercentage_WithTheRawValueBeside()
    {
        var (viewModel, engine, _, catalog) = Build();
        viewModel.SelectedCamera = Option(catalog, "ZWO");

        engine.Publish(ZwoConnected(gainPercent: 25));

        Assert.True(viewModel.IsGainEditable);
        Assert.Equal(25m, viewModel.GainPercent);
        Assert.Contains("150", viewModel.GainDetailText, StringComparison.Ordinal);
        Assert.Contains("600", viewModel.GainDetailText, StringComparison.Ordinal);
    }

    /// <summary>
    /// A connected ASCOM camera never shows gain, even though its model may be
    /// the very same ZWO: through ASCOM, gain is the driver's.
    /// </summary>
    [Fact]
    public void AConnectedAscomCamera_ShowsNoGainControl()
    {
        var (viewModel, engine, _, catalog) = Build();
        viewModel.SelectedCamera = Option(catalog, "ASCOM");

        engine.Publish(AscomConnected());

        Assert.False(viewModel.ShowGainControl);
        Assert.True(viewModel.HasCameraSetupDialog);
    }

    // ---- Setting gain ----

    [Fact]
    public void ChangingTheGain_SendsTheNewPercentage()
    {
        var (viewModel, engine, _, catalog) = Build();
        viewModel.SelectedCamera = Option(catalog, "ZWO");
        engine.Publish(ZwoConnected(gainPercent: 25));

        viewModel.GainPercent = 40m;

        Assert.Equal(40, Assert.Single(engine.Commands.OfType<SetCameraGainCommand>()).Percent);
    }

    /// <summary>
    /// The control writes its value back whenever its binding refreshes; each of
    /// those must not become a command and a settings write.
    /// </summary>
    [Fact]
    public void WritingBackTheSameGain_SendsNothing()
    {
        var (viewModel, engine, _, catalog) = Build();
        viewModel.SelectedCamera = Option(catalog, "ZWO");
        engine.Publish(ZwoConnected(gainPercent: 25));

        viewModel.GainPercent = 25m;

        Assert.Empty(engine.Commands.OfType<SetCameraGainCommand>());
    }

    // ---- Remembered per camera ----

    [Fact]
    public void AGainChange_IsRememberedForThatCamera()
    {
        var (viewModel, engine, store, catalog) = Build();
        viewModel.SelectedCamera = Option(catalog, "ZWO");
        engine.Publish(ZwoConnected());

        engine.Publish(new CameraGainChangedEvent(new CameraGainDescription(40, 240, 0, 600)));

        Assert.Equal(40, store.Saved.ForCamera("ZWO/1A2B3C4D5E6F7081")?.GainPercent);
    }

    [Fact]
    public void AReadoutChange_IsRememberedForThatCamera()
    {
        var (viewModel, engine, store, catalog) = Build();
        viewModel.SelectedCamera = Option(catalog, "ZWO");
        engine.Publish(ZwoConnected(readout: 1));

        engine.Publish(new ReadoutModeChangedEvent(0, "RAW8", 8));

        Assert.Equal(0, store.Saved.ForCamera("ZWO/1A2B3C4D5E6F7081")?.ReadoutModeIndex);
    }

    /// <summary>
    /// Reconnecting the same camera puts back its readout mode and gain. Without
    /// this both would revert to the driver's defaults and change the data with
    /// nothing on screen to say so.
    /// </summary>
    [Fact]
    public void ReconnectingACamera_RestoresItsReadoutAndGain()
    {
        AppSettings remembered = AppSettings.Empty.WithCamera(
            "ZWO/1A2B3C4D5E6F7081", c => c with { ReadoutModeIndex = 0, GainPercent = 40 });
        var (viewModel, engine, _, catalog) = Build(remembered);
        viewModel.SelectedCamera = Option(catalog, "ZWO");

        engine.Publish(ZwoConnected(readout: 1, gainPercent: 25));

        Assert.Equal(0, Assert.Single(engine.Commands.OfType<SetReadoutModeCommand>()).Index);
        Assert.Equal(40, Assert.Single(engine.Commands.OfType<SetCameraGainCommand>()).Percent);
    }

    /// <summary>
    /// Keyed by serial number: a second camera of the same model does not
    /// inherit the first one's gain, which was chosen for a different sensor.
    /// </summary>
    [Fact]
    public void AnotherCameraOfTheSameModel_DoesNotInheritTheSettings()
    {
        AppSettings remembered = AppSettings.Empty.WithCamera(
            "ZWO/1A2B3C4D5E6F7081", c => c with { ReadoutModeIndex = 0, GainPercent = 40 });
        var (viewModel, engine, _, catalog) = Build(remembered);
        viewModel.SelectedCamera = Option(catalog, "ZWO");

        engine.Publish(ZwoConnected(serial: "FFFFFFFFFFFFFFFF", readout: 1, gainPercent: 25));

        Assert.Empty(engine.Commands.OfType<SetReadoutModeCommand>());
        Assert.Empty(engine.Commands.OfType<SetCameraGainCommand>());
    }

    /// <summary>
    /// A readout mode stored before settings were kept per camera still reaches
    /// the camera it was chosen on -- the one connected last time -- and no
    /// other: index 1 on that camera is not index 1 on another.
    /// </summary>
    [Fact]
    public void AReadoutModeStoredTheOldWay_ReachesOnlyTheCameraItWasChosenOn()
    {
        AppSettings old = AppSettings.Empty with
        {
            ReadoutModeIndex = 1,
            CameraProviderName = "ASCOM",
            CameraDeviceId = AscomId,
        };

        var (sameCamera, sameEngine, _, catalog) = Build(old);
        sameCamera.SelectedCamera = Option(catalog, "ASCOM");
        sameEngine.Publish(AscomConnected(readout: 0));
        Assert.Equal(1, Assert.Single(sameEngine.Commands.OfType<SetReadoutModeCommand>()).Index);

        var (otherCamera, otherEngine, _, otherCatalog) = Build(old);
        otherCamera.SelectedCamera = Option(otherCatalog, "ZWO");
        otherEngine.Publish(ZwoConnected(readout: 0));
        Assert.Empty(otherEngine.Commands.OfType<SetReadoutModeCommand>());
    }
}
