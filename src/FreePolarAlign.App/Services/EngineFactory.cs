using FreePolarAlign.Core.Alignment;
using FreePolarAlign.Core.Engine;
using FreePolarAlign.Devices;
using FreePolarAlign.Devices.Simulated;
using FreePolarAlign.Devices.Simulated.SyntheticSky;
using FreePolarAlign.Session;
using FreePolarAlign.Solving;

namespace FreePolarAlign.App.Services;

/// <summary>What starting up produced: an engine ready to drive, and anything the user needs to be told before using it.</summary>
/// <param name="Warning">
/// Non-null when something is missing but not fatal -- currently only "no
/// Watney quad database" (D13). The app still starts and shows this
/// prominently rather than failing to launch or, worse, launching silently
/// broken: every capture would otherwise fail with an opaque solver error the
/// first time the user tries the sequence.
/// </param>
/// <param name="OwnedDisposables">
/// Everything created here that owns a resource, in an order safe to dispose
/// in sequence. The caller (the App class) is responsible for disposing these
/// when the window closes -- this factory only constructs.
/// </param>
public sealed record EngineStartupResult(
    IAlignmentEngine Engine,
    SessionConfiguration DefaultConfiguration,
    string? Warning,
    IReadOnlyList<IDisposable> OwnedDisposables);

/// <summary>
/// Builds the default engine the app drives: the virtual observatory
/// (<see cref="SimulatedDeviceProvider"/>, per the brief's "drive by default so
/// it can be run and demonstrated with no hardware") plus the embedded Watney
/// solver (D3), pointed at a real quad database directory if one is present.
///
/// Real hardware providers are a Phase 4+/D4 plugin concern and are
/// deliberately not wired up here: the App project's job for this phase is the
/// UI shell and the simulated loop it can always demonstrate.
/// </summary>
public static class EngineFactory
{
    /// <summary>D13: where the bundled core packs actually live.</summary>
    private static readonly string[] CorePackUrls =
    {
        "https://github.com/Jusas/WatneyAstrometry/releases/download/watneyqdb3/watneyqdb-00-07-20-v3.zip",
        "https://github.com/Jusas/WatneyAstrometry/releases/download/watneyqdb3/watneyqdb-08-09-20-v3.zip",
    };

    public static EngineStartupResult Create()
    {
        string quadDatabaseDirectory = ResolveQuadDatabaseDirectory();
        string? warning = HasUsableQuadDatabase(quadDatabaseDirectory) ? null : BuildMissingDatabaseWarning(quadDatabaseDirectory);

        StarCatalog catalog = StarCatalog.LoadCsv(Path.Combine(AppContext.BaseDirectory, "fixtures", "tycho2_subset.csv"));

        // A plausible mid-northern demo site and a deliberately non-trivial
        // injected misalignment, so a freshly-launched app has something worth
        // aligning rather than starting from a suspiciously perfect zero.
        var site = new GeodeticLocation(LatitudeDegrees: 51.5, LongitudeDegrees: -0.1, HeightMeters: 50.0);
        var misalignment = new MountMisalignment(AltitudeErrorArcminutes: 27.0, AzimuthErrorArcminutes: -19.0);
        var mountOptions = new SimulatedMountOptions(site, misalignment);
        var provider = new SimulatedDeviceProvider(catalog, mountOptions);

        ICamera camera = provider.OpenCamera("sim-camera");
        IMount mount = provider.OpenMount("sim-mount");
        var solver = new WatneyPlateSolver(quadDatabaseDirectory);
        var session = new AlignmentSession(camera, mount, solver);

        var configuration = new SessionConfiguration(
            SiteLatitudeDegrees: site.LatitudeDegrees,
            SiteLongitudeDegrees: site.LongitudeDegrees,
            SiteHeightMeters: site.HeightMeters,
            MinimumCapturePoints: SmallCircleFitter.MinimumObservations,
            ExposureDuration: TimeSpan.FromSeconds(2));

        return new EngineStartupResult(
            session,
            configuration,
            warning,
            new IDisposable[] { session, camera, mount, solver });
    }

    /// <summary>Env var override first, matching the pattern the end-to-end tests already use; otherwise the D13 default install location.</summary>
    private static string ResolveQuadDatabaseDirectory()
    {
        string? overridden = Environment.GetEnvironmentVariable("FPA_QUADDB_DIR");
        return !string.IsNullOrWhiteSpace(overridden)
            ? overridden
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".free-polar-align", "quaddb");
    }

    private static bool HasUsableQuadDatabase(string directory) =>
        Directory.Exists(directory) && Directory.EnumerateFiles(directory, "*.qdb", SearchOption.AllDirectories).Any();

    private static string BuildMissingDatabaseWarning(string directory) =>
        $"No Watney quad database found at '{directory}'. Plate solves will fail until one is installed. " +
        "Download the bundled core packs (D13) and extract each one -- including its .qdbindex sidecar, " +
        "which Watney requires alongside the .qdb files -- into that directory:" +
        Environment.NewLine + string.Join(Environment.NewLine, CorePackUrls) +
        Environment.NewLine + "Or set the FPA_QUADDB_DIR environment variable to point at an existing database.";
}
