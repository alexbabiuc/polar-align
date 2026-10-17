using FreePolarAlign.App.ViewModels;
using FreePolarAlign.Core.Engine;
using FreePolarAlign.Session;
using FreePolarAlign.Tests.Support;
using Xunit;

namespace FreePolarAlign.Tests.App;

/// <summary>
/// D36: Abort stops the mount's drive, so it is offered only with a mount to
/// stop. Stop sequence stays available either way.
/// </summary>
public class AbortUiTests
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
            new SessionConfiguration(6, 70.0), new TestClock());

        return (viewModel, engine);
    }

    private static DeviceConnectedEvent CameraConnected(bool hasSetupDialog) => new(
        DeviceKind.Camera, "ASCOM", "ASCOM.Simulator.Camera", "ASCOM Camera", "ASCOM / ASCOM.Simulator.Camera",
        new CameraDescription(3.8, 2737, 2053, HasSetupDialog: hasSetupDialog));

    private static readonly DeviceConnectedEvent MountConnected = new(
        DeviceKind.Mount, "Simulator", "sim-mount", "Simulated Mount", "Simulator / sim-mount", CanSlew: true);

    [Fact]
    public void WithoutAMount_AbortIsNotOfferedButStopIs()
    {
        var (viewModel, engine) = Build();
        engine.Publish(
            CameraConnected(hasSetupDialog: false),
            new SessionStartedEvent(new SessionConfiguration(6, 70.0), SequenceMode.Unconnected));

        Assert.True(viewModel.CancelCommand.CanExecute(null));
        Assert.False(viewModel.AbortCommand.CanExecute(null));
    }

    [Fact]
    public void WithAMountMidSequence_AbortIsOffered()
    {
        var (viewModel, engine) = Build();
        engine.Publish(
            CameraConnected(hasSetupDialog: false),
            MountConnected,
            new SessionStartedEvent(new SessionConfiguration(6, 70.0)));

        Assert.True(viewModel.AbortCommand.CanExecute(null));
    }
}
