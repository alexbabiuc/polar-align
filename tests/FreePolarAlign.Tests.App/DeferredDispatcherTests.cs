using FreePolarAlign.App.ViewModels;
using FreePolarAlign.Core.Engine;
using FreePolarAlign.Session;
using Xunit;

namespace FreePolarAlign.Tests.App;

/// <summary>
/// The view model against a dispatcher that actually defers, which is what
/// Avalonia's does.
///
/// These tests exist because of a bug that every other test in this project was
/// blind to. A single command routinely publishes several events -- connecting a
/// camera reports the device and then its plate scale; connecting a mount
/// reports the device and then its position -- and they arrive synchronously,
/// one after another, while the UI queue still holds the earlier ones. The view
/// model reduced each event against the state it could see at publish time and
/// posted the result, so every event of a command was folded against the same
/// stale state and the last post won.
///
/// The visible consequence was a camera that would not stay connected: its
/// plate scale arrived, the connection itself was overwritten away, and
/// everything gated on having a camera stayed disabled. Nothing detected it,
/// because a test dispatcher that runs actions immediately makes the two
/// orderings identical. So the dispatcher here queues, exactly as the real one
/// does.
/// </summary>
public class DeferredDispatcherTests
{
    /// <summary>
    /// A dispatcher that queues rather than running inline, and runs its queue
    /// in arrival order when drained -- the two properties of Avalonia's that
    /// matter here.
    /// </summary>
    private sealed class DeferredDispatcher
    {
        private readonly Queue<Action> _queue = new();

        public void Post(Action action) => _queue.Enqueue(action);

        public int Pending => _queue.Count;

        public void Drain()
        {
            while (_queue.Count > 0)
            {
                _queue.Dequeue()();
            }
        }
    }

    /// <summary>An engine that publishes whatever a test hands it, synchronously, as a real session does.</summary>
    private sealed class ScriptedEngine : IAlignmentEngine, IDisposable
    {
        private readonly List<IObserver<EngineEvent>> _observers = new();

        public IObservable<EngineEvent> Events => new Subscribable(_observers);

        public ValueTask SendAsync(EngineCommand command, CancellationToken cancellationToken = default) =>
            ValueTask.CompletedTask;

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

        public void Dispose() => _observers.Clear();

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

    private static (MainWindowViewModel ViewModel, ScriptedEngine Engine, DeferredDispatcher Dispatcher) Build()
    {
        var engine = new ScriptedEngine();
        var dispatcher = new DeferredDispatcher();

        var viewModel = new MainWindowViewModel(
            engine,
            catalog: null,
            settingsStore: null,
            settings: null,
            log: null,
            warnings: null,
            new SessionConfiguration(6, 70.0),
            postToUiThread: dispatcher.Post);

        return (viewModel, engine, dispatcher);
    }

    private static readonly DeviceConnectedEvent CameraConnected = new(
        DeviceKind.Camera, "Simulator", "sim-camera", "Simulated Camera", "Simulator / sim-camera",
        new CameraDescription(3.8, 2737, 2053));

    private static readonly EquipmentConfiguredEvent ScaleKnown = new(100.0, false, 7.84, 3.72);

    private static readonly DeviceConnectedEvent MountConnected = new(
        DeviceKind.Mount, "Simulator", "sim-mount", "Simulated Mount", "Simulator / sim-mount", CanSlew: true);

    private static readonly MountStatusEvent Status = new(
        145.9, 68.1, MountTrackingState.Tracking, MeridianSide.West);

    /// <summary>
    /// Two events from one command, and both have to survive. This is the exact
    /// pair that connecting a camera produces.
    /// </summary>
    [Fact]
    public void ConnectingACamera_LeavesItConnectedAndItsScaleKnown()
    {
        var (viewModel, engine, dispatcher) = Build();

        engine.Publish(CameraConnected, ScaleKnown);
        dispatcher.Drain();

        Assert.True(viewModel.IsCameraConnected, "the connection was overwritten by the event that followed it");
        Assert.Equal(100.0, viewModel.State.FocalLengthMillimetres);
        Assert.Equal(3.8, viewModel.State.CameraPixelSizeMicrons);
    }

    /// <summary>
    /// And the same pair for the mount, whose second event is a status poll.
    /// Losing the connection here would also lose whether it can slew at all,
    /// which decides between automatic and manual mode (D9/D10).
    /// </summary>
    [Fact]
    public void ConnectingAMount_LeavesItConnectedWithItsPositionAndSlewSupport()
    {
        var (viewModel, engine, dispatcher) = Build();

        engine.Publish(MountConnected, Status);
        dispatcher.Drain();

        Assert.True(viewModel.IsMountConnected);
        Assert.True(viewModel.State.MountCanSlew);
        Assert.Equal(MountTrackingState.Tracking, viewModel.State.MountTracking);
        Assert.Equal(145.9, viewModel.State.MountRaDegrees);
    }

    /// <summary>
    /// The whole opening sequence in one go, which is what the user actually
    /// does. Every step's effect has to be present at the end -- and in
    /// particular the button that starts a sequence has to become available,
    /// since that is the symptom the user sees when any of this is lost.
    /// </summary>
    [Fact]
    public void AfterTheFullSetupSequence_ASequenceCanBeStarted()
    {
        var (viewModel, engine, dispatcher) = Build();

        engine.Publish(
            CameraConnected,
            ScaleKnown,
            MountConnected,
            Status,
            new SiteConfiguredEvent(51.5, -0.1, 50.0));

        dispatcher.Drain();

        Assert.True(viewModel.IsCameraConnected);
        Assert.True(viewModel.IsMountConnected);
        Assert.True(viewModel.IsSiteConfigured);
        Assert.True(
            viewModel.StartCommand.CanExecute(null),
            "everything a sequence needs is connected and confirmed, so starting must be possible");
    }

    /// <summary>
    /// Nothing is applied until the queue runs. Stated explicitly because it is
    /// what makes this suite different from the others: if the dispatcher here
    /// ran inline, none of these tests could fail.
    /// </summary>
    [Fact]
    public void TheDispatcherReallyDefers()
    {
        var (viewModel, engine, dispatcher) = Build();

        engine.Publish(CameraConnected, ScaleKnown);

        Assert.Equal(2, dispatcher.Pending);
        Assert.False(viewModel.IsCameraConnected);

        dispatcher.Drain();
        Assert.True(viewModel.IsCameraConnected);
    }

    /// <summary>
    /// Every event is folded in, in arrival order. Order matters beyond tidiness:
    /// a withheld result followed by a fresh estimate means something different
    /// from the reverse, and the log has to read as the sequence actually
    /// happened.
    /// </summary>
    [Fact]
    public void EventsAreFoldedInArrivalOrder()
    {
        var (viewModel, engine, dispatcher) = Build();

        engine.Publish(
            new AlignmentUpdatedEvent(new AlignmentEstimate(3.0, 2.0, 3.6, 0.2, 0.3, 0.25, 0.5)),
            new AlignmentWithheldEvent("Declination moved between captures."),
            new AlignmentUpdatedEvent(new AlignmentEstimate(1.0, 0.5, 1.1, 0.2, 0.3, 0.25, 0.4)));

        dispatcher.Drain();

        // The last word was a trusted estimate, so that is what stands.
        Assert.NotNull(viewModel.State.CurrentEstimate);
        Assert.Equal(1.1, viewModel.State.CurrentEstimate!.TotalErrorArcminutes, precision: 6);
        Assert.Null(viewModel.State.WithheldReason);
        Assert.Equal(3, viewModel.Log.Count);
    }

    /// <summary>
    /// And the reverse order gives the opposite outcome -- the withdrawal has to
    /// win when it comes last, or a stale number stays on screen as though it
    /// were current, which is the failure D11 exists to prevent.
    /// </summary>
    [Fact]
    public void AWithdrawalArrivingLast_ClearsTheEstimate()
    {
        var (viewModel, engine, dispatcher) = Build();

        engine.Publish(
            new AlignmentUpdatedEvent(new AlignmentEstimate(1.0, 0.5, 1.1, 0.2, 0.3, 0.25, 0.4)),
            new AlignmentWithheldEvent("Declination moved between captures."));

        dispatcher.Drain();

        Assert.Null(viewModel.State.CurrentEstimate);
        Assert.NotNull(viewModel.State.WithheldReason);
    }

    /// <summary>
    /// A proposal seeds the editable coordinate fields with the decimal form, so
    /// that confirming without editing sends back exactly what was proposed and
    /// the engine can tell an acceptance from an override (D18).
    /// </summary>
    [Fact]
    public void AProposalSeedsTheEditableCoordinates()
    {
        var (viewModel, engine, dispatcher) = Build();

        engine.Publish(new SlewProposedEvent(
            2, 6, 145.912345, 68.098765, 26.0, 71.0, RequiresMotion: true, "Slew to 26° west."));

        dispatcher.Drain();

        Assert.True(CoordinateText.TryParseRightAscension(viewModel.ProposalRaText, out double ra));
        Assert.True(CoordinateText.TryParseDeclination(viewModel.ProposalDecText, out double dec));

        // Well inside the arcsecond the engine treats as "unedited".
        Assert.Equal(145.912345, ra, tolerance: 0.1 / 3600.0);
        Assert.Equal(68.098765, dec, tolerance: 0.1 / 3600.0);
    }
}
