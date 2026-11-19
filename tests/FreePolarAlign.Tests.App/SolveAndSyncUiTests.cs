using FreePolarAlign.App.ViewModels;
using FreePolarAlign.Core.Engine;
using FreePolarAlign.Tests.Support;
using Xunit;

namespace FreePolarAlign.Tests.App;

/// <summary>
/// D45: Solve & sync is offered between sequences only, and only with a mount
/// to sync; its outcome is shown in the right-hand column.
/// </summary>
public class SolveAndSyncUiTests
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

        private sealed class Subscribable(List<IObserver<EngineEvent>> observers) : IObservable<EngineEvent>
        {
            public IDisposable Subscribe(IObserver<EngineEvent> observer)
            {
                observers.Add(observer);
                return new Unsubscriber(observers, observer);
            }

            private sealed class Unsubscriber(List<IObserver<EngineEvent>> observers, IObserver<EngineEvent> observer)
                : IDisposable
            {
                public void Dispose() => observers.Remove(observer);
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

    private static readonly DeviceConnectedEvent CameraConnected = new(
        DeviceKind.Camera, "Simulator", "sim-camera", "Simulated Camera", "Simulator / sim-camera",
        new CameraDescription(3.8, 2737, 2053));

    private static readonly DeviceConnectedEvent MountConnected = new(
        DeviceKind.Mount, "Simulator", "sim-mount", "Simulated Mount", "Simulator / sim-mount", CanSlew: true);

    private static MountStatusEvent MountStatus(MountTrackingState tracking, bool moving = false) =>
        new(80.0, 20.0, tracking, MeridianSide.West, moving);

    private static readonly MountStatusEvent Tracking = MountStatus(MountTrackingState.Tracking);

    private static readonly SiteConfiguredEvent SiteConfigured = new(45.0, 15.0, 200.0);

    [Fact]
    public void WithoutAMount_TheButtonIsHiddenAndUnavailable()
    {
        var (viewModel, engine) = Build();
        engine.Publish(CameraConnected);

        Assert.False(viewModel.IsMountConnected);
        Assert.False(viewModel.SolveAndSyncCommand.CanExecute(null));
    }

    [Fact]
    public void BetweenSequences_WithACameraAndAMount_ItIsOfferedAndSendsTheCommand()
    {
        var (viewModel, engine) = Build();
        engine.Publish(CameraConnected, MountConnected, Tracking);

        Assert.True(viewModel.IsMountConnected);
        Assert.True(viewModel.SolveAndSyncCommand.CanExecute(null));

        viewModel.SolveAndSyncCommand.Execute(null);
        Assert.IsType<SolveAndSyncCommand>(engine.Commands.Last());
    }

    /// <summary>Untracked or moving, the telescope leaves the solved position before the sync reaches it.</summary>
    [Theory]
    [InlineData(MountTrackingState.Stopped, false)]
    [InlineData(MountTrackingState.Unknown, false)]
    [InlineData(MountTrackingState.Tracking, true)]
    public void UnlessTheMountIsTrackingAndStill_ItIsNotOffered(MountTrackingState tracking, bool moving)
    {
        var (viewModel, engine) = Build();
        engine.Publish(CameraConnected, MountConnected, MountStatus(tracking, moving));

        Assert.True(viewModel.IsMountConnected);
        Assert.False(viewModel.SolveAndSyncCommand.CanExecute(null));
    }

    [Fact]
    public void DuringASequence_ItIsNotOffered()
    {
        var (viewModel, engine) = Build();
        engine.Publish(CameraConnected, MountConnected, new SessionStartedEvent(new SessionConfiguration(6, 70.0)));

        Assert.False(viewModel.SolveAndSyncCommand.CanExecute(null));
    }

    /// <summary>
    /// One sync at a time, but a sequence may start over one: the engine then
    /// cancels it, and the panel shows nothing for the cancellation.
    /// </summary>
    [Fact]
    public void WhileASyncRuns_StartIsOfferedAndCancellingItLeavesThePanelEmpty()
    {
        var (viewModel, engine) = Build();
        engine.Publish(CameraConnected, MountConnected, Tracking, SiteConfigured, new MountSyncStartedEvent());

        Assert.True(viewModel.HasSyncResult);
        Assert.True(viewModel.IsSyncInProgress);
        Assert.False(viewModel.SolveAndSyncCommand.CanExecute(null));
        Assert.True(viewModel.StartCommand.CanExecute(null));

        engine.Publish(
            new MountSyncCancelledEvent("A sequence was started."),
            new SessionStartedEvent(new SessionConfiguration(6, 70.0)),
            new SessionCompletedEvent());

        Assert.False(viewModel.HasSyncResult);
        Assert.Null(viewModel.State.Sync);
    }

    [Fact]
    public void ASync_ShowsWhereTheMountNowPoints()
    {
        var (viewModel, engine) = Build();
        engine.Publish(CameraConnected, MountConnected, Tracking,
            new MountSyncStartedEvent(),
            new MountSyncedEvent(307.5, -12.5, 14.26));

        Assert.True(viewModel.HasSyncResult);
        Assert.True(viewModel.IsSyncSucceeded);
        Assert.False(viewModel.IsSyncFailed);
        Assert.Equal("MOUNT SYNCED", viewModel.SyncTitle);
        Assert.Contains("RA 20:30:00", viewModel.SyncCoordinatesText);
        Assert.Contains("Dec -12:30:00", viewModel.SyncCoordinatesText);
        Assert.Contains("14.3' out", viewModel.SyncDetail);
        Assert.True(viewModel.SolveAndSyncCommand.CanExecute(null));
    }

    [Theory]
    [InlineData(MountSyncStage.Solve, "SOLVE FAILED")]
    [InlineData(MountSyncStage.Sync, "SYNC FAILED")]
    public void AFailure_SaysWhichHalfFailedAndWhy(MountSyncStage stage, string title)
    {
        var (viewModel, engine) = Build();
        engine.Publish(CameraConnected, MountConnected,
            new MountSyncStartedEvent(),
            new MountSyncFailedEvent(stage, "Because of the test."));

        Assert.True(viewModel.IsSyncFailed);
        Assert.StartsWith(title, viewModel.SyncTitle);
        Assert.Contains("NOT SYNCED", viewModel.SyncTitle);
        Assert.Equal("Because of the test.", viewModel.SyncDetail);
        Assert.Empty(viewModel.SyncCoordinatesText);
    }

    /// <summary>A result about the mount is not left up once a sequence, or another mount, is in play.</summary>
    [Fact]
    public void TheResult_GoesWhenASequenceStartsOrTheMountGoes()
    {
        var (viewModel, engine) = Build();
        engine.Publish(CameraConnected, MountConnected, new MountSyncedEvent(10.0, 20.0, 1.0));
        Assert.True(viewModel.HasSyncResult);

        engine.Publish(new SessionStartedEvent(new SessionConfiguration(6, 70.0)));
        Assert.False(viewModel.HasSyncResult);

        engine.Publish(new SessionCompletedEvent(), new MountSyncedEvent(10.0, 20.0, 1.0));
        Assert.True(viewModel.HasSyncResult);

        engine.Publish(new DeviceDisconnectedEvent(DeviceKind.Mount));
        Assert.False(viewModel.HasSyncResult);
    }
}
