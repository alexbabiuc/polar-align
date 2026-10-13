using FreePolarAlign.App.ViewModels;
using FreePolarAlign.Core.Engine;
using FreePolarAlign.Session;
using Xunit;

namespace FreePolarAlign.Tests.App;

/// <summary>
/// The settings window edits a copy: nothing is remembered, and nothing in the
/// main window changes, unless Save is pressed on valid values. Saving is also
/// what puts the site and the focal length in use, and from then on they are
/// applied without asking (D19 as revised, D34).
/// </summary>
public class SettingsWindowTests
{
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

    private static readonly SessionConfiguration Defaults = new(6, 70.0);

    private static readonly StoredSite Bucharest = new(44.4268, 26.1025, 85.0);

    private static readonly DeviceConnectedEvent CameraConnected = new(
        DeviceKind.Camera, "Simulated", "cam", "Simulated camera", "Simulator",
        new CameraDescription(3.76, 1200, 800, Array.Empty<CameraReadoutModeDescription>(), null, HasSetupDialog: false));

    private static (MainWindowViewModel ViewModel, RecordingEngine Engine, InMemorySettingsStore Store) BuildMain(
        AppSettings? settings = null)
    {
        var engine = new RecordingEngine();
        var store = new InMemorySettingsStore();
        var viewModel = new MainWindowViewModel(engine, catalog: null, store, settings, log: null, warnings: null, Defaults);
        return (viewModel, engine, store);
    }

    private static (SettingsViewModel Window, List<AppSettings> Saved, Func<int> Closed) Build(AppSettings? current = null)
    {
        var saved = new List<AppSettings>();
        int closed = 0;
        var window = new SettingsViewModel(current ?? AppSettings.Empty, Defaults, saved.Add, () => closed++);
        return (window, saved, () => closed);
    }

    [Fact]
    public void WithNothingRememberedItShowsTheBuiltInDefaultsAndTheInternalSolver()
    {
        (SettingsViewModel window, _, _) = Build();

        Assert.Equal("6", window.CapturePointsText);
        Assert.Equal(SolverKind.Internal, window.BlindSolver.Kind);
        Assert.Equal(SolverKind.Internal, window.NearSolver.Kind);
        Assert.Equal(string.Empty, window.AstapPath);
        Assert.Equal("20", window.BlindTimeoutText);
        Assert.Equal("5", window.NearTimeoutText);
    }

    [Fact]
    public void SolveTimeoutsAreSaved_WithADecimalPointOrComma()
    {
        (SettingsViewModel window, List<AppSettings> saved, _) = Build();

        window.BlindTimeoutText = "30";
        window.NearTimeoutText = "2,5";
        window.SaveCommand.Execute(null);

        AppSettings result = Assert.Single(saved);
        Assert.Equal(30.0, result.BlindSolveTimeoutSeconds);
        Assert.Equal(2.5, result.NearSolveTimeoutSeconds);
    }

    [Theory]
    [InlineData("0", "5")]
    [InlineData("20", "601")]
    [InlineData("soon", "5")]
    [InlineData("20", "")]
    public void AnUnusableTimeout_IsRefusedAndNothingIsSaved(string blind, string near)
    {
        (SettingsViewModel window, List<AppSettings> saved, Func<int> closed) = Build();

        window.BlindTimeoutText = blind;
        window.NearTimeoutText = near;
        window.SaveCommand.Execute(null);

        Assert.Empty(saved);
        Assert.Equal(0, closed());
        Assert.Contains("between 1 and 600 seconds", window.Error);
    }

    [Fact]
    public void SavingStoresEveryFieldAndClosesTheWindow()
    {
        (SettingsViewModel window, List<AppSettings> saved, Func<int> closed) = Build();

        window.CapturePointsText = "9";
        window.BlindSolver = SettingsViewModel.SolverOptions.Single(o => o.Kind == SolverKind.Astap);
        window.NearSolver = SettingsViewModel.SolverOptions.Single(o => o.Kind == SolverKind.Ps3);
        window.AspsPath = "  /opt/asps  ";
        window.AstapPath = "/opt/astap";
        window.Ps3Path = "";
        window.SaveCommand.Execute(null);

        AppSettings result = Assert.Single(saved);
        Assert.Equal(9, result.DefaultCapturePoints);
        Assert.Equal(SolverKind.Astap, result.BlindSolver);
        Assert.Equal(SolverKind.Ps3, result.NearSolver);
        Assert.Equal("/opt/asps", result.AspsPath);
        Assert.Equal("/opt/astap", result.AstapPath);
        Assert.Null(result.Ps3Path);
        Assert.Equal(1, closed());
    }

    [Theory]
    [InlineData("2")]
    [InlineData("many")]
    [InlineData("")]
    public void AnInvalidCountIsRefusedAndTheWindowStaysOpen(string text)
    {
        (SettingsViewModel window, List<AppSettings> saved, Func<int> closed) = Build();

        window.CapturePointsText = text;
        window.SaveCommand.Execute(null);

        Assert.Empty(saved);
        Assert.NotNull(window.Error);
        Assert.Equal(0, closed());
    }

    [Fact]
    public void CancellingStoresNothing()
    {
        (SettingsViewModel window, List<AppSettings> saved, Func<int> closed) = Build();

        window.CapturePointsText = "12";
        window.CancelCommand.Execute(null);

        Assert.Empty(saved);
        Assert.Equal(1, closed());
    }

    [Fact]
    public void SavingKeepsTheSettingsTheWindowDoesNotEdit()
    {
        var site = new StoredSite(44.4, 26.1, 85.0);
        (SettingsViewModel window, List<AppSettings> saved, _) = Build(new AppSettings { Site = site, ExposureSeconds = 2.0 });

        window.SaveCommand.Execute(null);

        Assert.Equal(site, saved.Single().Site);
        Assert.Equal(2.0, saved.Single().ExposureSeconds);
    }

    [Fact]
    public void SavedSettingsAreWrittenOutAndRefillTheSequencePanel()
    {
        var store = new InMemorySettingsStore();
        var viewModel = new MainWindowViewModel(
            engine: null, catalog: null, settingsStore: store, settings: null, log: null, warnings: null,
            Defaults);

        SettingsViewModel window = viewModel.CreateSettingsViewModel(() => { });
        window.CapturePointsText = "11";
        window.SaveCommand.Execute(null);

        Assert.Equal(11, store.Saved.DefaultCapturePoints);
        Assert.Equal("11", viewModel.CapturePointsText);
    }

    /// <summary>A solver chosen here is used from the next frame, not the next launch.</summary>
    [Fact]
    public void SavedSolverChoiceReachesTheRunningApplication()
    {
        var changes = new List<AppSettings>();
        var viewModel = new MainWindowViewModel(
            engine: null, catalog: null, settingsStore: new InMemorySettingsStore(), settings: null, log: null, warnings: null,
            Defaults,
            settingsChanged: changes.Add);

        SettingsViewModel window = viewModel.CreateSettingsViewModel(() => { });
        window.BlindSolver = SettingsViewModel.SolverOptions.Single(o => o.Kind == SolverKind.Astap);
        window.SaveCommand.Execute(null);

        Assert.Equal(SolverKind.Astap, changes.Single().BlindSolver);
    }

    // ---- Sequence ----

    [Fact]
    public void TheSweepIsRememberedAndUsedByTheNextSequence_WithADecimalPointOrComma()
    {
        var (viewModel, _, store) = BuildMain();

        SettingsViewModel window = viewModel.CreateSettingsViewModel(() => { });
        Assert.Equal("70", window.SweepText);
        window.SweepText = "92,5";
        window.SaveCommand.Execute(null);

        Assert.Equal(92.5, store.Saved.SweepDegrees);
        Assert.Equal("92.5", viewModel.SweepText);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("-10")]
    [InlineData("wide")]
    public void AnUnusableSweep_IsRefusedAndNothingIsSaved(string text)
    {
        (SettingsViewModel window, List<AppSettings> saved, Func<int> closed) = Build();

        window.SweepText = text;
        window.SaveCommand.Execute(null);

        Assert.Empty(saved);
        Assert.Equal(0, closed());
        Assert.Contains("sweep", window.Error);
    }

    // ---- Site and focal length in the window ----

    [Fact]
    public void ARememberedSiteAndFocalLengthFillTheFields()
    {
        (SettingsViewModel window, _, _) = Build(new AppSettings { Site = Bucharest, FocalLengthMillimetres = 382.5 });

        Assert.Equal("44.42680", window.LatitudeText);
        Assert.Equal("26.10250", window.LongitudeText);
        Assert.Equal("85", window.HeightText);
        Assert.Equal("382.5", window.FocalLengthText);
    }

    [Fact]
    public void ASiteAndFocalLengthAreSaved_AndAnEmptyHeightIsSeaLevel()
    {
        (SettingsViewModel window, List<AppSettings> saved, _) = Build();

        window.LatitudeText = "51,5";
        window.LongitudeText = "-0.12";
        window.FocalLengthText = "400";
        window.SaveCommand.Execute(null);

        AppSettings result = Assert.Single(saved);
        Assert.Equal(new StoredSite(51.5, -0.12, 0.0), result.Site);
        Assert.Equal(400.0, result.FocalLengthMillimetres);
        Assert.False(result.IsFocalLengthSolved);
    }

    /// <summary>A first run with no site yet: Save must still work for everything else.</summary>
    [Fact]
    public void NoSiteAtAll_IsAllowed()
    {
        (SettingsViewModel window, List<AppSettings> saved, _) = Build();

        window.SaveCommand.Execute(null);

        Assert.Null(Assert.Single(saved).Site);
    }

    [Theory]
    [InlineData("91", "10", "Latitude")]
    [InlineData("", "10", "Latitude")]
    [InlineData("45", "181", "Longitude")]
    [InlineData("45", "", "Longitude")]
    public void AnUnusableSite_IsRefusedAndNothingIsSaved(string latitude, string longitude, string named)
    {
        (SettingsViewModel window, List<AppSettings> saved, Func<int> closed) = Build();

        window.LatitudeText = latitude;
        window.LongitudeText = longitude;
        window.SaveCommand.Execute(null);

        Assert.Empty(saved);
        Assert.Equal(0, closed());
        Assert.Contains(named, window.Error);
    }

    /// <summary>
    /// Saving to change something else must not demote a measured focal length
    /// to an entered one: the flag decides how tightly the solver may bound its
    /// search.
    /// </summary>
    [Fact]
    public void AnUneditedMeasuredFocalLength_StaysMeasured()
    {
        (SettingsViewModel window, List<AppSettings> saved, _) = Build(
            new AppSettings { FocalLengthMillimetres = 382.46, IsFocalLengthSolved = true });

        Assert.True(window.IsFocalLengthMeasured);
        window.SaveCommand.Execute(null);

        Assert.Equal(382.46, saved.Single().FocalLengthMillimetres);
        Assert.True(saved.Single().IsFocalLengthSolved);
    }

    [Fact]
    public void DuringASequence_TheSiteIsKeptWhateverIsTyped()
    {
        var saved = new List<AppSettings>();
        var window = new SettingsViewModel(new AppSettings { Site = Bucharest }, Defaults, saved.Add, () => { }, canChangeSite: false);

        window.LatitudeText = "10";
        window.SaveCommand.Execute(null);

        Assert.Equal(Bucharest, saved.Single().Site);
    }

    // ---- Applied without asking ----

    /// <summary>The site is entered once; after that, starting the application is enough.</summary>
    [Fact]
    public void ARememberedSite_IsAppliedAtStartup()
    {
        var (_, engine, _) = BuildMain(new AppSettings { Site = Bucharest });

        ConfigureSiteCommand sent = Assert.Single(engine.Commands.OfType<ConfigureSiteCommand>());
        Assert.Equal(new ConfigureSiteCommand(44.4268, 26.1025, 85.0), sent);
    }

    [Fact]
    public void WithNoRememberedSite_NothingIsSentAtStartup()
    {
        var (_, engine, _) = BuildMain();

        Assert.Empty(engine.Commands);
    }

    [Fact]
    public void SavingTheFirstSite_PutsItInUse()
    {
        var (viewModel, engine, store) = BuildMain();

        SettingsViewModel window = viewModel.CreateSettingsViewModel(() => { });
        window.LatitudeText = "44.4268";
        window.LongitudeText = "26.1025";
        window.HeightText = "85";
        window.SaveCommand.Execute(null);

        Assert.Equal(Bucharest, store.Saved.Site);
        Assert.Equal(new ConfigureSiteCommand(44.4268, 26.1025, 85.0), Assert.Single(engine.Commands.OfType<ConfigureSiteCommand>()));
    }

    [Fact]
    public void SavingWithTheSiteInUseUnchanged_DoesNotSendItAgain()
    {
        var (viewModel, engine, _) = BuildMain(new AppSettings { Site = Bucharest });
        engine.Publish(new SiteConfiguredEvent(44.4268, 26.1025, 85.0, null));
        engine.Commands.Clear();

        SettingsViewModel window = viewModel.CreateSettingsViewModel(() => { });
        window.SaveCommand.Execute(null);

        Assert.Empty(engine.Commands.OfType<ConfigureSiteCommand>());
    }

    /// <summary>
    /// The engine takes a focal length only once a camera is connected, which is
    /// why the old Apply button was disabled until then. A remembered one is now
    /// handed over the moment a camera connects.
    /// </summary>
    [Fact]
    public void ARememberedFocalLength_IsAppliedWhenACameraConnects()
    {
        var (_, engine, _) = BuildMain(new AppSettings { FocalLengthMillimetres = 382.5 });
        Assert.Empty(engine.Commands.OfType<ConfigureFocalLengthCommand>());

        engine.Publish(CameraConnected);

        Assert.Equal(382.5, Assert.Single(engine.Commands.OfType<ConfigureFocalLengthCommand>()).FocalLengthMillimetres);
    }

    /// <summary>
    /// A camera reports no focal length when it connects, and that report used
    /// to be remembered: the stored figure was wiped before anything could
    /// apply it.
    /// </summary>
    [Fact]
    public void ACameraReportingNoFocalLength_DoesNotEraseTheRememberedOne()
    {
        var (_, engine, store) = BuildMain(new AppSettings { FocalLengthMillimetres = 382.5 });

        engine.Publish(CameraConnected, new EquipmentConfiguredEvent(null, false, null, null));

        Assert.Equal(382.5, store.Saved.FocalLengthMillimetres);
    }

    [Fact]
    public void AFocalLengthSavedWithACameraConnected_IsAppliedAtOnce()
    {
        var (viewModel, engine, _) = BuildMain();
        engine.Publish(CameraConnected);

        SettingsViewModel window = viewModel.CreateSettingsViewModel(() => { });
        window.FocalLengthText = "400";
        window.SaveCommand.Execute(null);

        Assert.Equal(400.0, Assert.Single(engine.Commands.OfType<ConfigureFocalLengthCommand>()).FocalLengthMillimetres);
    }

    [Fact]
    public void AFocalLengthSavedWithNoCamera_WaitsForOne()
    {
        var (viewModel, engine, store) = BuildMain();

        SettingsViewModel window = viewModel.CreateSettingsViewModel(() => { });
        window.FocalLengthText = "400";
        window.SaveCommand.Execute(null);

        Assert.Empty(engine.Commands.OfType<ConfigureFocalLengthCommand>());
        Assert.Equal(400.0, store.Saved.FocalLengthMillimetres);
    }

    /// <summary>
    /// The main window showed only the engine's figure, which is empty until a
    /// camera connects, so a focal length just saved read as "not set -- solves
    /// will be blind".
    /// </summary>
    [Fact]
    public void AFocalLengthWaitingForACamera_IsShownAsWaiting_NotAsMissing()
    {
        var (viewModel, engine, _) = BuildMain();
        Assert.Contains("not set", viewModel.FocalLengthSummary);

        var changed = new List<string?>();
        viewModel.PropertyChanged += (_, e) => changed.Add(e.PropertyName);
        SettingsViewModel window = viewModel.CreateSettingsViewModel(() => { });
        window.FocalLengthText = "400";
        window.SaveCommand.Execute(null);

        Assert.Contains(nameof(MainWindowViewModel.FocalLengthSummary), changed);
        Assert.Equal("400.0 mm (applied when a camera connects)", viewModel.FocalLengthSummary);

        engine.Publish(CameraConnected, new EquipmentConfiguredEvent(400.0, false, 1.94, 0.4));

        Assert.Equal("400.0 mm (as entered)", viewModel.FocalLengthSummary);
    }
}
