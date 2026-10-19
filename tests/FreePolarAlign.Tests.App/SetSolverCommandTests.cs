using FreePolarAlign.App.Services;
using FreePolarAlign.Session;
using Xunit;

namespace FreePolarAlign.Tests.App;

/// <summary>
/// The installer's way of choosing a solver when no index pack was chosen
/// (D39). Run against a real settings file, because what is guaranteed is what
/// ends up on disk.
/// </summary>
public sealed class SetSolverCommandTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "FreePolarAlign.Tests", Guid.NewGuid().ToString("N"));
    private readonly JsonFileSettingsStore _store;
    private readonly StringWriter _error = new();

    public SetSolverCommandTests()
    {
        _store = new JsonFileSettingsStore(Path.Combine(_directory, "settings.json"));
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    private int? Run(params string[] args) => SetSolverCommand.TryRun(args, _store, _error);

    [Fact]
    public void WithoutTheSwitch_TheApplicationStartsAsNormal()
    {
        Assert.Null(Run());
        Assert.Null(Run("--something-else"));
        Assert.False(File.Exists(_store.Location));
    }

    [Fact]
    public void SetsTheSolver()
    {
        Assert.Equal(SetSolverCommand.Succeeded, Run("--set-solver", "Astap"));

        Assert.Equal(SolverKind.Astap, _store.Load().Settings.Solver);
    }

    /// <summary>
    /// A reinstall runs this over a file the user has been filling in for months.
    /// </summary>
    [Fact]
    public void LeavesEverythingElseTheUserSet()
    {
        var site = new StoredSite(45.5, 25.5, 300.0);
        Assert.Null(_store.Save(AppSettings.Empty with { Site = site, FocalLengthMillimetres = 240.0, DefaultCapturePoints = 5 }));

        Assert.Equal(SetSolverCommand.Succeeded, Run("--set-solver", "Ps3"));

        AppSettings saved = _store.Load().Settings;
        Assert.Equal(site, saved.Site);
        Assert.Equal(240.0, saved.FocalLengthMillimetres);
        Assert.Equal(5, saved.DefaultCapturePoints);
        Assert.Equal(SolverKind.Ps3, saved.Solver);
    }

    [Fact]
    public void Internal_IsStoredTheWayAFreshInstallStoresIt()
    {
        Assert.Null(_store.Save(AppSettings.Empty with { Solver = SolverKind.Asps }));

        Assert.Equal(SetSolverCommand.Succeeded, Run("--set-solver", "internal"));

        Assert.Null(_store.Load().Settings.Solver);
    }

    /// <summary>
    /// Saving the defaults over a file that could not be read would wipe the
    /// site and optics it holds, which may be fine, from a newer version.
    /// </summary>
    [Fact]
    public void AnUnreadableSettingsFile_IsLeftExactlyAsItWas()
    {
        Directory.CreateDirectory(_directory);
        const string unreadable = "{ \"solver\": \"SomethingFromANewerVersion\" }";
        File.WriteAllText(_store.Location, unreadable);

        Assert.Equal(SetSolverCommand.SettingsUnreadable, Run("--set-solver", "Astap"));
        Assert.Equal(unreadable, File.ReadAllText(_store.Location));
    }

    /// <summary>D30: solvers are chosen by name, so a number must not pick one.</summary>
    [Theory]
    [InlineData("1")]
    [InlineData("Watney")]
    [InlineData("")]
    public void AnythingButASolverName_IsRefused(string name)
    {
        Assert.Equal(SetSolverCommand.BadArguments, Run("--set-solver", name));
        Assert.False(File.Exists(_store.Location));
    }

    [Fact]
    public void AMissingOrExtraArgument_IsRefused()
    {
        Assert.Equal(SetSolverCommand.BadArguments, Run("--set-solver"));
        Assert.Equal(SetSolverCommand.BadArguments, Run("--set-solver", "Astap", "Asps"));
        Assert.False(File.Exists(_store.Location));
    }
}
