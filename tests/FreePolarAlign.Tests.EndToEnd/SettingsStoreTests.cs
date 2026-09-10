using FreePolarAlign.Session;
using Xunit;

namespace FreePolarAlign.Tests.EndToEnd;

/// <summary>
/// The settings file is the one piece of state that survives between observing
/// sessions, so its failure modes are all failures the user meets in the dark:
/// a latitude quietly carried forward from a hand-edited file would bias every
/// altitude figure reported, and a corrupt file that stopped the application
/// starting would end the night. These tests pin the contract down at the file
/// level -- real paths, real JSON -- in a throwaway directory, never the user's
/// profile.
/// </summary>
public sealed class SettingsStoreTests : IDisposable
{
    private readonly string _directory;

    public SettingsStoreTests()
    {
        _directory = Path.Combine(Path.GetTempPath(), "fpa-settings-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_directory);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_directory))
            {
                Directory.Delete(_directory, recursive: true);
            }
        }
        catch (IOException)
        {
            // A leftover temp directory is not worth failing a test over.
        }
    }

    private string PathIn(params string[] parts) =>
        Path.Combine(new[] { _directory }.Concat(parts).ToArray());

    private static AppSettings FullSettings() => new()
    {
        Site = new StoredSite(44.4268, 26.1025, 85.0),
        FocalLengthMillimetres = 382.5,
        IsFocalLengthSolved = true,
        CameraProviderName = "ASCOM",
        CameraDeviceId = "ASCOM.Simulator.Camera",
        MountProviderName = "ALPACA",
        MountDeviceId = "alpaca://localhost:11111/telescope/0",
    };

    /// <summary>
    /// The whole point of the file is that a measured focal length and a
    /// painstakingly typed latitude do not have to be entered again tomorrow. If
    /// any single field failed to survive the round trip the user would be asked
    /// to retype it while believing it was already stored -- and the solved flag
    /// matters as much as the number, because it decides how tight a scale hint
    /// the solver may be given.
    /// </summary>
    [Fact]
    public void SavedSettings_SurviveAReloadFieldForField()
    {
        string path = PathIn("settings.json");
        var store = new JsonFileSettingsStore(path);
        AppSettings saved = FullSettings();

        Assert.Null(store.Save(saved));

        SettingsLoadResult result = new JsonFileSettingsStore(path).Load();

        Assert.Null(result.Warning);
        AppSettings loaded = result.Settings;
        Assert.NotNull(loaded.Site);
        Assert.Equal(44.4268, loaded.Site!.LatitudeDegrees, precision: 9);
        Assert.Equal(26.1025, loaded.Site.LongitudeDegrees, precision: 9);
        Assert.Equal(85.0, loaded.Site.HeightMeters, precision: 9);
        Assert.Equal(382.5, loaded.FocalLengthMillimetres!.Value, precision: 9);
        Assert.True(loaded.IsFocalLengthSolved);
        Assert.Equal("ASCOM", loaded.CameraProviderName);
        Assert.Equal("ASCOM.Simulator.Camera", loaded.CameraDeviceId);
        Assert.Equal("ALPACA", loaded.MountProviderName);
        Assert.Equal("alpaca://localhost:11111/telescope/0", loaded.MountDeviceId);
    }

    /// <summary>
    /// A first run is the normal case, not an error. Warning about the absence of
    /// a file the software has never written would train the user to ignore the
    /// warnings that do matter.
    /// </summary>
    [Fact]
    public void MissingFile_LoadsDefaultsSilently()
    {
        SettingsLoadResult result = new JsonFileSettingsStore(PathIn("absent.json")).Load();

        Assert.Same(AppSettings.Empty, result.Settings);
        Assert.Null(result.Warning);
    }

    /// <summary>
    /// A truncated or hand-mangled file must not stop the application starting --
    /// but it must not be swallowed either. The user is about to be asked for a
    /// site they believe is stored, so the message has to name the file, or they
    /// have no way to find out what went wrong.
    /// </summary>
    [Fact]
    public void UnparseableFile_LoadsDefaultsAndNamesTheFile()
    {
        string path = PathIn("broken.json");
        File.WriteAllText(path, "{ \"site\": { \"latitudeDegrees\": ");

        SettingsLoadResult result = new JsonFileSettingsStore(path).Load();

        Assert.Same(AppSettings.Empty, result.Settings);
        Assert.NotNull(result.Warning);
        Assert.Contains(path, result.Warning!);
    }

    /// <summary>
    /// An impossible latitude means the site cannot be trusted, and a site the
    /// software cannot trust must be dropped rather than clamped: every altitude
    /// figure it reports is measured against it. Nothing else in the file is
    /// implicated, though -- a usable focal length is still the saving this file
    /// exists for, so discarding it too would be an unnecessary retype.
    /// </summary>
    [Fact]
    public void OutOfRangeLatitude_DiscardsOnlyTheSite()
    {
        string path = PathIn("bad-latitude.json");
        File.WriteAllText(
            path,
            """
            {
              "site": { "latitudeDegrees": 200, "longitudeDegrees": 26.1, "heightMeters": 85 },
              "focalLengthMillimetres": 382.5,
              "isFocalLengthSolved": true
            }
            """);

        SettingsLoadResult result = new JsonFileSettingsStore(path).Load();

        Assert.Null(result.Settings.Site);
        Assert.NotNull(result.Warning);
        Assert.Equal(382.5, result.Settings.FocalLengthMillimetres!.Value, precision: 9);
        Assert.True(result.Settings.IsFocalLengthSolved);
    }

    /// <summary>
    /// A focal length that cannot be real would turn every later blind search
    /// into a bounded search around the wrong scale, which fails slower and less
    /// obviously than having no hint at all. The solved flag has to go with it:
    /// left behind, it would claim solver-grade confidence for a value that is
    /// now absent.
    /// </summary>
    [Theory]
    [InlineData(-5.0)]
    [InlineData(1e9)]
    public void ImpossibleFocalLength_IsDiscardedWithItsSolvedFlag(double focalLength)
    {
        string path = PathIn("bad-focal-length.json");
        File.WriteAllText(
            path,
            $$"""
            {
              "focalLengthMillimetres": {{focalLength:R}},
              "isFocalLengthSolved": true
            }
            """);

        SettingsLoadResult result = new JsonFileSettingsStore(path).Load();

        Assert.Null(result.Settings.FocalLengthMillimetres);
        Assert.False(result.Settings.IsFocalLengthSolved);
        Assert.NotNull(result.Warning);
    }

    /// <summary>
    /// The settings path is under a directory the software owns and may never
    /// have created. If the first save of a fresh install failed for that reason
    /// the user would lose everything from the session that just succeeded.
    /// </summary>
    [Fact]
    public void Save_CreatesTheDirectoriesItNeeds()
    {
        string path = PathIn("never", "created", "settings.json");
        var store = new JsonFileSettingsStore(path);

        Assert.Null(store.Save(FullSettings()));
        Assert.True(File.Exists(path));
    }

    /// <summary>
    /// The write goes via a temporary file so an interruption leaves the previous
    /// settings intact. A successful write must still clean up after itself: a
    /// stale sibling holding an older copy of the site is exactly the sort of
    /// thing a user later mistakes for a backup worth restoring.
    /// </summary>
    [Fact]
    public void Save_LeavesNoTemporaryFileBehind()
    {
        string path = PathIn("settings.json");
        var store = new JsonFileSettingsStore(path);

        Assert.Null(store.Save(FullSettings()));
        Assert.Null(store.Save(FullSettings() with { FocalLengthMillimetres = 400.0 }));

        Assert.False(File.Exists(path + ".tmp"));
        Assert.Empty(Directory.GetFiles(_directory, "*.tmp"));
    }

    /// <summary>
    /// Reloading a site is a convenience for whoever is typing, not evidence that
    /// the tripod is where it was last night. A store that reported the reloaded
    /// site as already confirmed would let a stored latitude follow a user to a
    /// different site and bias every figure by the difference, so the flag stays
    /// set even on settings that were just written by this same application.
    /// </summary>
    [Fact]
    public void ReloadedSite_StillNeedsConfirmation()
    {
        string path = PathIn("settings.json");
        var store = new JsonFileSettingsStore(path);
        store.Save(FullSettings());

        SettingsLoadResult result = store.Load();

        Assert.NotNull(result.Settings.Site);
        Assert.True(result.Settings.SiteNeedsConfirmation);
    }
}
