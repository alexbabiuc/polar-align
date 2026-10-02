using FreePolarAlign.App.ViewModels;
using FreePolarAlign.Session;
using Xunit;

namespace FreePolarAlign.Tests.App;

/// <summary>
/// The settings window edits a copy: nothing is remembered, and nothing in the
/// main window changes, unless Save is pressed on valid values.
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

    private static (SettingsViewModel Window, List<AppSettings> Saved, Func<int> Closed) Build(AppSettings? current = null)
    {
        var saved = new List<AppSettings>();
        int closed = 0;
        var window = new SettingsViewModel(current ?? AppSettings.Empty, 6, saved.Add, () => closed++);
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
            new FreePolarAlign.Core.Engine.SessionConfiguration(6, 70.0));

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
            new FreePolarAlign.Core.Engine.SessionConfiguration(6, 70.0),
            settingsChanged: changes.Add);

        SettingsViewModel window = viewModel.CreateSettingsViewModel(() => { });
        window.BlindSolver = SettingsViewModel.SolverOptions.Single(o => o.Kind == SolverKind.Astap);
        window.SaveCommand.Execute(null);

        Assert.Equal(SolverKind.Astap, changes.Single().BlindSolver);
    }
}
