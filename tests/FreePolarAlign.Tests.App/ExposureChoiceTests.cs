using FreePolarAlign.App.ViewModels;
using FreePolarAlign.Core.Engine;
using FreePolarAlign.Session;
using Xunit;

namespace FreePolarAlign.Tests.App;

/// <summary>
/// The exposure picker.
///
/// It exists because the application exposed no exposure control at all and
/// captured at a hardcoded two seconds. On a 105 mm lens under a moderately
/// bright sky that put the background at 57% of full well, which left 23 to 28
/// detectable stars against the 115 or more the same camera gave through other
/// software, and the solves failed. The setting has to reach the camera and it
/// has to survive a restart, because a choice that quietly reverts overnight is
/// how the same frame gets over-exposed twice.
/// </summary>
public class ExposureChoiceTests
{
    /// <summary>An engine that records what it was sent, and publishes whatever a test hands it.</summary>
    private sealed class RecordingEngine : IAlignmentEngine
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

    /// <summary>Everything connected and a confirmed site: as much as possible available.</summary>
    private static void MakeStartable(RecordingEngine engine) =>
        engine.Publish(
            new DeviceConnectedEvent(
                DeviceKind.Camera, "Simulator", "sim-camera", "Simulated Camera", "Simulator / sim-camera",
                new CameraDescription(3.8, 2737, 2053)),
            new DeviceConnectedEvent(
                DeviceKind.Mount, "Simulator", "sim-mount", "Simulated Mount", "Simulator / sim-mount", CanSlew: true),
            new SiteConfiguredEvent(44.43, 26.10, 85.0));

    private sealed class InMemorySettingsStore : ISettingsStore
    {
        public InMemorySettingsStore(AppSettings? initial = null) => Saved = initial ?? AppSettings.Empty;

        public AppSettings Saved { get; private set; }

        public int Writes { get; private set; }

        public string Location => "(in memory)";

        public SettingsLoadResult Load() => new(Saved, null);

        public string? Save(AppSettings settings)
        {
            Saved = settings;
            Writes++;
            return null;
        }
    }

    private static (MainWindowViewModel ViewModel, RecordingEngine Engine, InMemorySettingsStore Store) Build(
        AppSettings? settings = null)
    {
        var engine = new RecordingEngine();
        var store = new InMemorySettingsStore(settings);

        var viewModel = new MainWindowViewModel(
            engine,
            catalog: null,
            settingsStore: store,
            settings: settings,
            log: null,
            warnings: null,
            new SessionConfiguration(6, 70.0));

        return (viewModel, engine, store);
    }

    // ---- The list ----

    /// <summary>The values asked for, in order, and no others.</summary>
    [Fact]
    public void ThePickerOffersTheChosenValues()
    {
        double[] seconds = ExposureOption.All.Select(o => o.Duration.TotalSeconds).ToArray();

        Assert.Equal(new[] { 0.1, 0.2, 0.5, 1.0, 1.5, 2.0 }, seconds);
    }

    [Fact]
    public void EveryOptionIsLabelledInSeconds() =>
        Assert.All(ExposureOption.All, option => Assert.EndsWith(" s", option.Label, StringComparison.Ordinal));

    // ---- Reaching the camera ----

    private static ExposureOption Seconds(double seconds) =>
        ExposureOption.All.Single(o => o.Duration == TimeSpan.FromSeconds(seconds));

    /// <summary>
    /// The point of the control: the frames are taken at what the picker shows.
    /// The camera runs continuously (D26), so the choice goes to the engine as
    /// it is made rather than waiting for a sequence to start.
    /// </summary>
    [Fact]
    public void ChoosingAnExposure_SendsItToTheEngine()
    {
        var (viewModel, engine, _) = Build();

        viewModel.SelectedExposure = Seconds(0.5);

        Assert.Equal(TimeSpan.FromSeconds(0.5), Assert.Single(engine.Commands.OfType<SetExposureCommand>()).Duration);
    }

    /// <summary>
    /// Including mid-sequence (D22 as revised). Exposure changes how noisy a
    /// solved position is, not where it is, and a sky brightening under
    /// twilight has to be answerable without abandoning the sequence. The
    /// picker used to be disabled here.
    /// </summary>
    [Fact]
    public void TheExposure_CanBeChangedDuringASequence()
    {
        var (viewModel, engine, _) = Build();
        MakeStartable(engine);
        engine.Publish(new SessionStartedEvent(new SessionConfiguration(6, 70.0)));

        Assert.True(viewModel.IsExposureEditable);
        viewModel.SelectedExposure = Seconds(0.2);

        Assert.Equal(TimeSpan.FromSeconds(0.2), Assert.Single(engine.Commands.OfType<SetExposureCommand>()).Duration);
    }

    /// <summary>
    /// But not while the driver's window is open (D23): the engine is blocked
    /// behind it, and a change sent meanwhile would queue and land afterwards on
    /// a camera that has changed underneath it. Refused in the setter as well as
    /// by the disabled control, and nothing is remembered either, since nothing
    /// was applied.
    /// </summary>
    [Fact]
    public void TheExposure_CannotBeChangedWhileTheDriverWindowIsOpen()
    {
        var (viewModel, engine, store) = Build();
        engine.Publish(new CameraSetupDialogChangedEvent(IsOpen: true));

        Assert.False(viewModel.IsExposureEditable);
        viewModel.SelectedExposure = Seconds(0.2);

        Assert.Empty(engine.Commands.OfType<SetExposureCommand>());
        Assert.Equal(TimeSpan.FromSeconds(2), viewModel.SelectedExposure.Duration);
        Assert.Null(store.Saved.ExposureSeconds);
    }

    /// <summary>
    /// The picker is the authority. An engine reporting some other exposure --
    /// one built with its own default, say -- is sent the chosen one, rather
    /// than the picker quietly following it and the remembered choice being
    /// lost.
    /// </summary>
    [Fact]
    public void AnEngineReportingADifferentExposure_IsSentTheChosenOne()
    {
        var (viewModel, engine, _) = Build(AppSettings.Empty with { ExposureSeconds = 0.5 });

        engine.Publish(new ExposureChangedEvent(TimeSpan.FromSeconds(2)));

        Assert.Equal(TimeSpan.FromSeconds(0.5), Assert.Single(engine.Commands.OfType<SetExposureCommand>()).Duration);
        Assert.Equal(TimeSpan.FromSeconds(0.5), viewModel.SelectedExposure.Duration);
    }

    /// <summary>And one that agrees is left alone, so the echo of a change does not bounce back and forth.</summary>
    [Fact]
    public void AnEngineReportingTheChosenExposure_IsNotSentItAgain()
    {
        var (_, engine, _) = Build(AppSettings.Empty with { ExposureSeconds = 0.5 });

        engine.Publish(new ExposureChangedEvent(TimeSpan.FromSeconds(0.5)));

        Assert.Empty(engine.Commands.OfType<SetExposureCommand>());
    }

    // ---- Persistence ----

    [Fact]
    public void ChoosingAnExposure_StoresIt()
    {
        var (viewModel, _, store) = Build();

        viewModel.SelectedExposure = ExposureOption.All.Single(o => o.Duration == TimeSpan.FromSeconds(0.2));

        Assert.Equal(0.2, store.Saved.ExposureSeconds);
    }

    /// <summary>
    /// And it comes back, which is the half that matters in the field: the
    /// exposure is a judgement about the sky and the optics, and it does not
    /// change between one night and the next.
    /// </summary>
    [Fact]
    public void ARememberedExposure_IsPreselected()
    {
        var (viewModel, _, _) = Build(AppSettings.Empty with { ExposureSeconds = 1.5 });

        Assert.Equal(TimeSpan.FromSeconds(1.5), viewModel.SelectedExposure.Duration);
    }

    /// <summary>
    /// Nothing remembered falls back to the configuration the engine was built
    /// with, so the picker always shows what a sequence would actually use
    /// rather than a blank meaning "whatever the engine decides".
    /// </summary>
    [Fact]
    public void WithNothingRemembered_ThePickerShowsTheDefault()
    {
        var (viewModel, _, _) = Build();

        Assert.Equal(TimeSpan.FromSeconds(2), viewModel.SelectedExposure.Duration);
    }

    /// <summary>
    /// A value that is not on the list -- an older settings file, or one edited
    /// by hand -- snaps to the nearest listed one rather than being dropped.
    /// Round-tripping a double through JSON is a good way to silently lose a
    /// setting to an exact comparison.
    /// </summary>
    [Theory]
    [InlineData(0.09, 0.1)]
    [InlineData(0.3, 0.2)]
    [InlineData(0.9, 1.0)]
    [InlineData(30.0, 2.0)]
    public void AnUnlistedRememberedValue_SnapsToTheNearest(double stored, double expected)
    {
        var (viewModel, _, _) = Build(AppSettings.Empty with { ExposureSeconds = stored });

        Assert.Equal(expected, viewModel.SelectedExposure.Duration.TotalSeconds, precision: 6);
    }

    [Theory]
    [InlineData(null)]
    [InlineData(0.0)]
    [InlineData(-1.0)]
    [InlineData(double.NaN)]
    public void AnUnusableRememberedValue_IsIgnored(double? stored)
    {
        var (viewModel, _, _) = Build(AppSettings.Empty with { ExposureSeconds = stored });

        Assert.Equal(TimeSpan.FromSeconds(2), viewModel.SelectedExposure.Duration);
    }

    /// <summary>
    /// Avalonia hands a ComboBox's SelectedItem back as null while its list is
    /// being rebuilt. A null exposure has no meaning, and accepting one would
    /// throw at the next sequence start.
    /// </summary>
    [Fact]
    public void ANullSelection_KeepsTheLastRealChoice()
    {
        var (viewModel, _, _) = Build();
        viewModel.SelectedExposure = ExposureOption.All[0];

        viewModel.SelectedExposure = null!;

        Assert.Equal(ExposureOption.All[0], viewModel.SelectedExposure);
    }

    /// <summary>Re-choosing what is already chosen writes nothing, so the settings file is not rewritten on every ComboBox notification.</summary>
    [Fact]
    public void ReselectingTheSameExposure_WritesNothing()
    {
        var (viewModel, _, store) = Build();
        viewModel.SelectedExposure = ExposureOption.All[0];
        int writes = store.Writes;

        viewModel.SelectedExposure = ExposureOption.All[0];

        Assert.Equal(writes, store.Writes);
    }
}
