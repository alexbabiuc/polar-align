using System.Windows.Input;
using FreePolarAlign.App.ViewModels;
using FreePolarAlign.Core.Engine;
using FreePolarAlign.Session;
using Xunit;

namespace FreePolarAlign.Tests.App;

/// <summary>
/// The UI side of opening the camera driver's settings window.
///
/// The part worth pinning is the refusal. While that window is up the engine
/// holds its command gate, so anything the user clicks does not fail -- it
/// queues, and runs afterwards against a camera whose gain, offset or readout
/// have just changed. A frame captured that way looks like every other frame.
/// </summary>
public class CameraSetupUiTests
{
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
                return new Unsubscriber(_observers, observer);
            }

            private sealed class Unsubscriber : IDisposable
            {
                private readonly List<IObserver<EngineEvent>> _observers;
                private readonly IObserver<EngineEvent> _observer;

                public Unsubscriber(List<IObserver<EngineEvent>> observers, IObserver<EngineEvent> observer)
                {
                    _observers = observers;
                    _observer = observer;
                }

                public void Dispose() => _observers.Remove(_observer);
            }
        }
    }

    private static (MainWindowViewModel ViewModel, PublishingEngine Engine) Build()
    {
        var engine = new PublishingEngine();

        var viewModel = new MainWindowViewModel(
            engine,
            catalog: null,
            settingsStore: null,
            settings: null,
            log: null,
            warnings: null,
            new SessionConfiguration(6, 70.0));

        return (viewModel, engine);
    }

    private static DeviceConnectedEvent CameraConnected(bool hasSetupDialog) => new(
        DeviceKind.Camera, "ASCOM", "ASCOM.Simulator.Camera", "ASCOM Camera", "ASCOM / ASCOM.Simulator.Camera",
        new CameraDescription(3.8, 2737, 2053, HasSetupDialog: hasSetupDialog));

    private static readonly DeviceConnectedEvent MountConnected = new(
        DeviceKind.Mount, "Simulator", "sim-mount", "Simulated Mount", "Simulator / sim-mount", CanSlew: true);

    /// <summary>Every command the window binds a button to.</summary>
    private static IEnumerable<(string Name, ICommand Command)> AllCommands(MainWindowViewModel vm) => new[]
    {
        (nameof(vm.ConnectCameraCommand), (ICommand)vm.ConnectCameraCommand),
        (nameof(vm.DisconnectCameraCommand), vm.DisconnectCameraCommand),
        (nameof(vm.OpenCameraSetupCommand), vm.OpenCameraSetupCommand),
        (nameof(vm.ConnectMountCommand), vm.ConnectMountCommand),
        (nameof(vm.DisconnectMountCommand), vm.DisconnectMountCommand),
        (nameof(vm.ConfirmSiteCommand), vm.ConfirmSiteCommand),
        (nameof(vm.ApplyFocalLengthCommand), vm.ApplyFocalLengthCommand),
        (nameof(vm.StartCommand), vm.StartCommand),
        (nameof(vm.RecordSampleCommand), vm.RecordSampleCommand),
        (nameof(vm.SaveFrameCommand), vm.SaveFrameCommand),
        (nameof(vm.ConfirmProposalCommand), vm.ConfirmProposalCommand),
        (nameof(vm.RestoreProposalCoordinatesCommand), vm.RestoreProposalCoordinatesCommand),
        (nameof(vm.CancelCommand), vm.CancelCommand),
        (nameof(vm.AbortCommand), vm.AbortCommand),
    };

    // ---- Offering the button ----

    /// <summary>
    /// The button appears only once a camera whose driver has such a window is
    /// connected. Before that there is no driver to ask, and a button that is
    /// always there and usually dead teaches the user to ignore it.
    /// </summary>
    [Fact]
    public void WithNoCameraConnected_TheButtonIsNotOffered()
    {
        var (viewModel, _) = Build();

        Assert.False(viewModel.HasCameraSetupDialog);
        Assert.False(viewModel.OpenCameraSetupCommand.CanExecute(null));
    }

    [Fact]
    public void ConnectingADriverWithASettingsWindow_OffersTheButton()
    {
        var (viewModel, engine) = Build();

        engine.Publish(CameraConnected(hasSetupDialog: true));

        Assert.True(viewModel.HasCameraSetupDialog);
        Assert.True(viewModel.OpenCameraSetupCommand.CanExecute(null));
    }

    /// <summary>A camera without one -- the simulator, for instance -- does not get a button that would do nothing.</summary>
    [Fact]
    public void ConnectingADriverWithoutOne_DoesNotOfferTheButton()
    {
        var (viewModel, engine) = Build();

        engine.Publish(CameraConnected(hasSetupDialog: false));

        Assert.False(viewModel.HasCameraSetupDialog);
        Assert.False(viewModel.OpenCameraSetupCommand.CanExecute(null));
    }

    [Fact]
    public void DisconnectingTheCamera_WithdrawsTheButton()
    {
        var (viewModel, engine) = Build();
        engine.Publish(CameraConnected(hasSetupDialog: true));

        engine.Publish(new DeviceDisconnectedEvent(DeviceKind.Camera));

        Assert.False(viewModel.HasCameraSetupDialog);
    }

    [Fact]
    public void PressingTheButton_SendsTheCommand()
    {
        var (viewModel, engine) = Build();
        engine.Publish(CameraConnected(hasSetupDialog: true));

        viewModel.OpenCameraSetupCommand.Execute(null);

        Assert.Single(engine.Commands.OfType<OpenCameraSetupDialogCommand>());
    }

    // ---- Refusing everything while it is open ----

    /// <summary>
    /// The requirement, stated as a test: nothing at all can be done until the
    /// window closes.
    ///
    /// Enumerated over every command rather than spot-checked, because the
    /// failure this guards against is a thirteenth command added later with the
    /// condition left off -- and the window is disabled wholesale in the XAML,
    /// so a missing predicate would not show up by looking at the running app
    /// either.
    /// </summary>
    [Fact]
    public void WhileTheDialogIsOpen_NoCommandWillRun()
    {
        var (viewModel, engine) = Build();

        // A state where as much as possible would otherwise be available.
        engine.Publish(
            CameraConnected(hasSetupDialog: true),
            MountConnected,
            new SiteConfiguredEvent(44.43, 26.10, 85.0));

        Assert.Contains(AllCommands(viewModel), entry => entry.Command.CanExecute(null));

        engine.Publish(new CameraSetupDialogChangedEvent(IsOpen: true));

        Assert.True(viewModel.IsCameraSetupDialogOpen);
        Assert.All(
            AllCommands(viewModel),
            entry => Assert.False(entry.Command.CanExecute(null), $"{entry.Name} was still available"));
    }

    /// <summary>
    /// And everything comes back when it closes. A window that left the
    /// application permanently inert would be worse than not offering it.
    /// </summary>
    [Fact]
    public void WhenTheDialogCloses_TheApplicationWorksAgain()
    {
        var (viewModel, engine) = Build();
        engine.Publish(
            CameraConnected(hasSetupDialog: true),
            MountConnected,
            new SiteConfiguredEvent(44.43, 26.10, 85.0));

        engine.Publish(new CameraSetupDialogChangedEvent(IsOpen: true));
        engine.Publish(new CameraSetupDialogChangedEvent(IsOpen: false));

        Assert.False(viewModel.IsCameraSetupDialogOpen);
        Assert.True(viewModel.StartCommand.CanExecute(null));
        Assert.True(viewModel.OpenCameraSetupCommand.CanExecute(null));
    }

    /// <summary>The status line says what is going on, since the window may well have opened behind this one.</summary>
    [Fact]
    public void WhileTheDialogIsOpen_TheStatusLineSaysSo()
    {
        var (viewModel, engine) = Build();
        engine.Publish(CameraConnected(hasSetupDialog: true));

        engine.Publish(new CameraSetupDialogChangedEvent(IsOpen: true));

        Assert.Contains("settings window is open", viewModel.StatusMessage, StringComparison.Ordinal);
    }
}
