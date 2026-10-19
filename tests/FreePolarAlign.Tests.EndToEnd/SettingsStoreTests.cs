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
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
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
    /// A sweep that cannot be asked for costs only itself: the sequence then
    /// uses the built-in default.
    /// </summary>
    [Theory]
    [InlineData(0.0)]
    [InlineData(-30.0)]
    public void AnUnusableSweep_IsDiscarded_AndTheRestKept(double sweep)
    {
        string path = PathIn("settings.json");
        new JsonFileSettingsStore(path).Save(FullSettings() with { SweepDegrees = sweep });

        SettingsLoadResult result = new JsonFileSettingsStore(path).Load();

        Assert.Null(result.Settings.SweepDegrees);
        Assert.NotNull(result.Settings.Site);
        Assert.NotNull(result.Warning);
    }

    // ---- Per-camera settings ----

    /// <summary>
    /// Settings remembered per camera survive the file, keyed exactly as
    /// written. The key carries a serial number, and a naming policy that
    /// camel-cased it would silently orphan every camera's settings.
    /// </summary>
    [Fact]
    public void PerCameraSettings_SurviveAReload_UnderTheirExactKeys()
    {
        string path = PathIn("cameras.json");
        string zwo = AppSettings.CameraKey("ZWO", "1A2B3C4D5E6F7081", "ZWO ASI290MM Mini");
        string ascom = AppSettings.CameraKey("ASCOM", null, "ASCOM.ASICamera2.Camera");

        AppSettings settings = AppSettings.Empty
            .WithCamera(zwo, c => c with { ReadoutModeIndex = 1, GainPercent = 12 })
            .WithCamera(ascom, c => c with { ReadoutModeIndex = 0 });

        Assert.Null(new JsonFileSettingsStore(path).Save(settings));
        AppSettings reloaded = new JsonFileSettingsStore(path).Load().Settings;

        Assert.Equal(new CameraSettings(ReadoutModeIndex: 1, GainPercent: 12), reloaded.ForCamera(zwo));
        Assert.Equal(new CameraSettings(ReadoutModeIndex: 0), reloaded.ForCamera(ascom));
        Assert.Contains("\"ZWO/1A2B3C4D5E6F7081\"", File.ReadAllText(path), StringComparison.Ordinal);
    }

    /// <summary>
    /// A camera with a serial is keyed by it, so two of the same model do not
    /// share settings; one without falls back to the id it was enumerated under.
    /// </summary>
    [Fact]
    public void ACameraIsKeyedBySerial_WhenItHasOne()
    {
        Assert.Equal("ZWO/1A2B3C4D5E6F7081", AppSettings.CameraKey("ZWO", "1A2B3C4D5E6F7081", "ZWO ASI290MM Mini"));
        Assert.Equal("ZWO/ZWO ASI290MM Mini", AppSettings.CameraKey("ZWO", null, "ZWO ASI290MM Mini"));
    }

    /// <summary>
    /// Changing nothing returns the same instance, so the caller's "did anything
    /// change" check -- which compares instances -- does not rewrite the file on
    /// every notification just because a dictionary is always a new object.
    /// </summary>
    [Fact]
    public void AnUnchangedCameraSetting_ReturnsTheSameSettings()
    {
        AppSettings settings = AppSettings.Empty.WithCamera("ZWO/1", c => c with { GainPercent = 30 });

        Assert.Same(settings, settings.WithCamera("ZWO/1", c => c with { GainPercent = 30 }));
        Assert.NotSame(settings, settings.WithCamera("ZWO/1", c => c with { GainPercent = 31 }));
    }

    [Fact]
    public void AnOutOfRangeCameraSetting_IsDiscarded_AndTheRestKept()
    {
        string path = PathIn("bad-camera.json");
        File.WriteAllText(path, """
            { "cameras": { "ZWO/1": { "readoutModeIndex": 1, "gainPercent": 250 } } }
            """);

        SettingsLoadResult result = new JsonFileSettingsStore(path).Load();

        Assert.Equal(new CameraSettings(ReadoutModeIndex: 1, GainPercent: null), result.Settings.ForCamera("ZWO/1"));
        Assert.NotNull(result.Warning);
    }

    [Fact]
    public void SolverChoicesPathsAndDefaultPointsSurviveARestart()
    {
        string path = PathIn("solvers", "settings.json");
        var store = new JsonFileSettingsStore(path);
        var settings = new AppSettings
        {
            DefaultCapturePoints = 8,
            SweepDegrees = 92.5,
            Solver = SolverKind.Astap,
            AspsPath = "/opt/asps",
            AstapPath = "/opt/astap",
            Ps3Path = "/opt/ps3",
            BlindSolveTimeoutSeconds = 30.0,
            NearSolveTimeoutSeconds = 2.5,
        };

        Assert.Null(store.Save(settings));

        // Read back by name in the file, so a reordered enum cannot reassign a
        // stored choice to a different solver.
        Assert.Contains("\"astap\"", File.ReadAllText(path).ToLowerInvariant());
        Assert.Equal(settings, new JsonFileSettingsStore(path).Load().Settings);
    }

    [Fact]
    public void AnOutOfRangeSolveTimeout_IsDiscarded_AndTheDefaultUsed()
    {
        string path = PathIn("timeouts.json");
        Directory.CreateDirectory(_directory);
        File.WriteAllText(path, "{\"blindSolveTimeoutSeconds\": 0, \"nearSolveTimeoutSeconds\": 3}");

        SettingsLoadResult result = new JsonFileSettingsStore(path).Load();

        Assert.Null(result.Settings.BlindSolveTimeoutSeconds);
        Assert.Equal(TimeSpan.FromSeconds(20), result.Settings.BlindSolveTimeout);
        Assert.Equal(TimeSpan.FromSeconds(3), result.Settings.NearSolveTimeout);
        Assert.Contains("blind solve timeout", result.Warning);
    }

    /// <summary>
    /// Zero is a choice, not "unset": a mount that is still the moment it stops
    /// should not come back from a restart waiting the default two seconds.
    /// </summary>
    [Theory]
    [InlineData(0.0)]
    [InlineData(4.5)]
    public void TheSettleTimeSurvivesARestart(double seconds)
    {
        string path = PathIn("settle", "settings.json");
        var settings = new AppSettings { SettleSeconds = seconds };

        Assert.Null(new JsonFileSettingsStore(path).Save(settings));
        AppSettings loaded = new JsonFileSettingsStore(path).Load().Settings;

        Assert.Equal(settings, loaded);
        Assert.Equal(TimeSpan.FromSeconds(seconds), loaded.SettleDelay);
    }

    [Theory]
    [InlineData("-1")]
    [InlineData("600")]
    public void AnOutOfRangeSettleTime_IsDiscarded_AndTheRestKept(string stored)
    {
        string path = PathIn("settle-bad.json");
        Directory.CreateDirectory(_directory);
        File.WriteAllText(path, $"{{\"settleSeconds\": {stored}, \"sweepDegrees\": 80}}");

        SettingsLoadResult result = new JsonFileSettingsStore(path).Load();

        Assert.Null(result.Settings.SettleSeconds);
        Assert.Null(result.Settings.SettleDelay);
        Assert.Equal(80.0, result.Settings.SweepDegrees);
        Assert.Contains("settle time", result.Warning);
    }

    [Fact]
    public void ASolverThisVersionDoesNotKnowCostsOnlyThatSetting()
    {
        string path = PathIn("future.json");
        Directory.CreateDirectory(_directory);
        File.WriteAllText(path, "{\"focalLengthMillimetres\": 382.5, \"solver\": 99, \"defaultCapturePoints\": 1}");

        SettingsLoadResult result = new JsonFileSettingsStore(path).Load();

        Assert.Equal(382.5, result.Settings.FocalLengthMillimetres);
        Assert.Null(result.Settings.Solver);
        Assert.Null(result.Settings.DefaultCapturePoints);
        Assert.NotNull(result.Warning);
    }

    /// <summary>
    /// 0.0.31 and earlier stored a solver per job. A file from then keeps its
    /// choice when the two agreed, as the installer's --set-solver wrote them.
    /// </summary>
    [Fact]
    public void SeparateSolversThatAgreed_BecomeTheOneSolver_Silently()
    {
        string path = PathIn("legacy-same.json");
        Directory.CreateDirectory(_directory);
        File.WriteAllText(path, "{\"blindSolver\": \"Astap\", \"nearSolver\": \"Astap\", \"sweepDegrees\": 80}");

        SettingsLoadResult result = new JsonFileSettingsStore(path).Load();

        Assert.Equal(SolverKind.Astap, result.Settings.Solver);
        Assert.Equal(80.0, result.Settings.SweepDegrees);
        Assert.Null(result.Warning);
    }

    /// <summary>
    /// Where they differed, the one chosen over the built-in default is kept, and
    /// the user is told, since one of their choices has gone (D42).
    /// </summary>
    [Theory]
    [InlineData("\"blindSolver\": \"Asps\"", SolverKind.Asps)]
    [InlineData("\"nearSolver\": \"Astap\"", SolverKind.Astap)]
    [InlineData("\"blindSolver\": \"Internal\", \"nearSolver\": \"Ps3\"", SolverKind.Ps3)]
    [InlineData("\"blindSolver\": \"Asps\", \"nearSolver\": \"Astap\"", SolverKind.Asps)]
    public void SeparateSolversThatDiffered_KeepTheChosenOne_AndSaySo(string stored, SolverKind kept)
    {
        string path = PathIn("legacy-differ.json");
        Directory.CreateDirectory(_directory);
        File.WriteAllText(path, "{" + stored + "}");

        SettingsLoadResult result = new JsonFileSettingsStore(path).Load();

        Assert.Equal(kept, result.Settings.Solver);
        Assert.Contains($"{kept} was kept", result.Warning);

        // Said on one start, not on every one after.
        SettingsLoadResult next = new JsonFileSettingsStore(path).Load();
        Assert.Equal(kept, next.Settings.Solver);
        Assert.Null(next.Warning);
    }

    /// <summary>The old keys are read once and not written back.</summary>
    [Fact]
    public void SeparateSolvers_AreNotWrittenBack()
    {
        string path = PathIn("legacy-resave.json");
        Directory.CreateDirectory(_directory);
        File.WriteAllText(path, "{\"blindSolver\": \"Astap\", \"nearSolver\": \"Astap\"}");
        var store = new JsonFileSettingsStore(path);

        Assert.Null(store.Save(store.Load().Settings));

        string json = File.ReadAllText(path);
        Assert.DoesNotContain("blindSolver", json);
        Assert.Contains("\"solver\": \"Astap\"", json);
    }

    /// <summary>A file written since has its own answer, which the old keys do not override.</summary>
    [Fact]
    public void TheOneSolver_WinsOverLeftoverSeparateOnes()
    {
        string path = PathIn("legacy-both.json");
        Directory.CreateDirectory(_directory);
        File.WriteAllText(path, "{\"solver\": \"Ps3\", \"blindSolver\": \"Astap\"}");

        SettingsLoadResult result = new JsonFileSettingsStore(path).Load();

        Assert.Equal(SolverKind.Ps3, result.Settings.Solver);
        Assert.Null(result.Warning);
    }
}
