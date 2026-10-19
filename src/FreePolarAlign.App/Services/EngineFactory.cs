using FreePolarAlign.App.ViewModels;
using FreePolarAlign.Core.Engine;
using FreePolarAlign.Devices;
using FreePolarAlign.Devices.Simulated;
using FreePolarAlign.Devices.Simulated.SyntheticSky;
using FreePolarAlign.Session;
using FreePolarAlign.Solving;

namespace FreePolarAlign.App.Services;

/// <summary>What starting up produced.</summary>
/// <param name="Catalog">
/// Everything connectable. Handed to the view model rather than consumed here,
/// because which device to open is the user's decision and not startup's.
/// </param>
/// <param name="Settings">
/// What was remembered from last time. The view model applies the site in it
/// at startup and the focal length when a camera connects (D19 as revised).
/// </param>
/// <param name="Warnings">
/// Everything the user needs to be told before relying on the application:
/// a missing quad database (D13), a provider that would not enumerate (D4), a
/// settings file that could not be read. All non-fatal, all worth reading, none
/// of them a reason to fail to start -- a window that explains itself is far
/// more use than an application that will not open.
/// </param>
/// <param name="Solver">
/// The session's solver, kept so a solver choice saved in the settings window
/// can be applied to it while the application runs.
/// </param>
/// <param name="OwnedDisposables">
/// Everything created here that owns a resource, in an order safe to dispose
/// in sequence. The caller (the App class) is responsible for disposing these
/// when the window closes -- this factory only constructs.
/// </param>
public sealed record EngineStartupResult(
    IAlignmentEngine Engine,
    DeviceCatalog Catalog,
    ISettingsStore SettingsStore,
    AppSettings Settings,
    SessionLog Log,
    SessionConfiguration DefaultConfiguration,
    SelectedSolver Solver,
    IReadOnlyList<string> Warnings,
    IReadOnlyList<IDisposable> OwnedDisposables,
    TimeProvider Time);

/// <summary>
/// Assembles the application: the device catalogue (the virtual observatory
/// always, plus whatever plugins are installed -- D4), the solvers (D3: the
/// embedded Watney, or whichever external one the settings chose instead), the
/// settings file, and the session log.
///
/// Nothing is connected here and no site is assumed. Both are the user's
/// explicit acts once the window is up (D18, D19), which is also what makes the
/// window worth showing before any hardware is talking.
/// </summary>
public static class EngineFactory
{
    /// <summary>D13: where the bundled core packs actually live.</summary>
    private static readonly string[] CorePackUrls =
    {
        "https://github.com/Jusas/WatneyAstrometry/releases/download/watneyqdb3/watneyqdb-00-07-20-v3.zip",
        "https://github.com/Jusas/WatneyAstrometry/releases/download/watneyqdb3/watneyqdb-08-09-20-v3.zip",
    };

    /// <param name="time">
    /// The one clock everything built here runs on: the system's, passed by the
    /// application. With the site it is all the engine needs (D37).
    /// </param>
    public static EngineStartupResult Create(TimeProvider time)
    {
        ArgumentNullException.ThrowIfNull(time);
        var warnings = new List<string>();
        SessionLog log = SessionLog.Create(time);

        if (log.Problem is not null)
        {
            warnings.Add(log.Problem);
        }

        var settingsStore = new JsonFileSettingsStore();
        SettingsLoadResult loaded = settingsStore.Load();
        if (loaded.Warning is not null)
        {
            warnings.Add(loaded.Warning);
            log.Write(LogSeverity.Warning, loaded.Warning);
        }

        log.Write(LogSeverity.Info, $"Settings file: {settingsStore.Location}");

        string quadDatabaseDirectory = ResolveQuadDatabaseDirectory();
        if (!HasUsableQuadDatabase(quadDatabaseDirectory))
        {
            if (UsesInternalSolver(loaded.Settings))
            {
                string warning = BuildMissingDatabaseWarning(quadDatabaseDirectory);
                warnings.Add(warning);
                log.Write(LogSeverity.Warning, warning);
            }
            else
            {
                log.Write(LogSeverity.Info, "No Watney quad database is installed; none is needed while another solver is chosen for both jobs.");
            }
        }

        log.Write(LogSeverity.Info, $"Quad database directory: {quadDatabaseDirectory}");

        DeviceCatalog catalog = BuildCatalog(warnings, log, time);

        // Watney first, and a frame redrawn from its own detected stars if
        // Watney cannot match the original. Both attempts are written to the
        // session log: "could not solve" is a great deal easier to act on when
        // it says what was tried.
        var watney = new WatneyPlateSolver(quadDatabaseDirectory, time);
        var internalSolver = new RestampFallbackSolver(
            watney,
            Path.Combine(Path.GetTempPath(), "FreePolarAlign", "restamped"),
            time,
            message => log.Write(LogSeverity.Info, message));

        var solver = new SelectedSolver(
            internalSolver, time, message => log.Write(LogSeverity.Info, message));
        solver.Configure(loaded.Settings);

        // The focal length is carried forward but not its measured status: a
        // remembered figure is a good hint and a poor measurement, and the
        // distinction decides how tightly the solver is allowed to bound its
        // search. It is re-measured from the first solve of the night anyway.
        //
        // The remembered exposure is the engine's starting one, snapped to the
        // picker's list exactly as the picker snaps it (D22), so the two agree
        // from the first frame. Handing it over later by command would work too,
        // but the camera starts exposing the moment it connects (D26), and the
        // frame already under way would be taken at the default -- on the sky
        // that motivated D22, a frame swamped at two seconds.
        var options = new AlignmentSessionOptions(
            ExposureDuration: ExposureOption.Nearest(loaded.Settings.ExposureSeconds)?.Duration ?? default,
            EquipmentProfile: null,
            ApplicationName: AppVersion.ProductName,
            ApplicationVersion: AppVersion.Identifier());

        var session = new AlignmentSession(catalog, solver, time, options);

        var configuration = new SessionConfiguration(
            CapturePoints: loaded.Settings.DefaultCapturePoints ?? options.CaptureCount,
            RequestedSweepDegrees: loaded.Settings.SweepDegrees ?? options.SweepDegrees,
            SettleDelay: loaded.Settings.SettleDelay);

        return new EngineStartupResult(
            session,
            catalog,
            settingsStore,
            loaded.Settings,
            log,
            configuration,
            solver,
            warnings,
            new IDisposable[] { session, solver, log },
            time);
    }

    /// <summary>
    /// The simulator is always present, so the application can be run and
    /// demonstrated with no hardware attached -- and so that a regression in the
    /// session logic shows up on a laptop at noon rather than at a telescope in
    /// the dark.
    /// </summary>
    private static DeviceCatalog BuildCatalog(List<string> warnings, SessionLog log, TimeProvider time)
    {
        var providers = new List<IDeviceProvider>();

        try
        {
            StarCatalog catalog = StarCatalog.LoadCsv(
                Path.Combine(AppContext.BaseDirectory, "fixtures", "tycho2_subset.csv"));

            // A plausible mid-northern demo site and a deliberately non-trivial
            // injected misalignment, so a freshly-launched app has something
            // worth aligning rather than starting from a suspiciously perfect
            // zero. The software is told none of this: discovering it is the job.
            var site = new GeodeticLocation(LatitudeDegrees: 51.5, LongitudeDegrees: -0.1, HeightMeters: 50.0);
            var misalignment = new Core.Alignment.MountMisalignment(
                AltitudeErrorArcminutes: 27.0, AzimuthErrorArcminutes: -19.0);

            providers.Add(new SimulatedDeviceProvider(catalog, new SimulatedMountOptions(site, misalignment), time));
        }
        catch (Exception ex) when (ex is IOException or FormatException or UnauthorizedAccessException)
        {
            string warning =
                $"The simulated observatory could not load its star catalogue: {ex.Message}. " +
                "Real devices will still work; the simulator will not appear in the device lists.";
            warnings.Add(warning);
            log.Write(LogSeverity.Warning, warning);
        }

        DeviceCatalog result = DeviceCatalog.Create(providers, PluginsDirectory());

        foreach (string problem in result.Problems)
        {
            warnings.Add(problem);
            log.Write(LogSeverity.Warning, problem);
        }

        // Logged and not shown: a vendor plugin with no vendor library is the
        // normal state for anyone who does not own that brand of camera.
        foreach (string note in result.Notes)
        {
            log.Write(LogSeverity.Info, note);
        }

        log.Write(
            LogSeverity.Info,
            $"Devices discovered: {result.Cameras.Count} camera(s), {result.Mounts.Count} mount(s).");

        return result;
    }

    private static string PluginsDirectory() => Path.Combine(AppContext.BaseDirectory, "plugins");

    private static string ResolveQuadDatabaseDirectory() => ResolveQuadDatabaseDirectory(
        Environment.GetEnvironmentVariable("FPA_QUADDB_DIR"),
        AppContext.BaseDirectory,
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        HasUsableQuadDatabase);

    /// <summary>
    /// Where the quad database is, tried in order: an explicit override, a copy
    /// bundled next to the executable, then the D13 default install location.
    ///
    /// The bundled-next-to-the-exe case exists for a fully offline distribution
    /// (a USB stick, a machine that will never see the installer that would
    /// normally populate the default location): the database and the
    /// application then travel and run together with nothing to configure,
    /// which matters because this location is the one place a user cannot be
    /// asked to set an environment variable or run a setup script first. It is
    /// checked before the user-profile default, not instead of it, so an
    /// existing install at the default location is left alone if a build
    /// happens not to carry its own copy.
    ///
    /// Takes every input as a parameter, including the "is this directory
    /// usable" check, so the precedence itself -- the part actually worth
    /// getting right -- can be tested without touching a real filesystem or a
    /// real environment variable.
    /// </summary>
    internal static string ResolveQuadDatabaseDirectory(
        string? environmentOverride,
        string applicationDirectory,
        string userProfileDirectory,
        Func<string, bool> hasUsableQuadDatabase)
    {
        if (!string.IsNullOrWhiteSpace(environmentOverride))
        {
            return environmentOverride;
        }

        string bundled = Path.Combine(applicationDirectory, "quaddb");
        if (hasUsableQuadDatabase(bundled))
        {
            return bundled;
        }

        return Path.Combine(userProfileDirectory, ".free-polar-align", "quaddb");
    }

    /// <summary>
    /// Whether Watney is the solver. A missing database is a problem only
    /// then: an installation that chose ASTAP instead of downloading packs
    /// (D39) would otherwise be told on every start that something is broken.
    /// </summary>
    internal static bool UsesInternalSolver(AppSettings settings) =>
        (settings.Solver ?? SolverKind.Internal) == SolverKind.Internal;

    private static bool HasUsableQuadDatabase(string directory) =>
        Directory.Exists(directory) && Directory.EnumerateFiles(directory, "*.qdb", SearchOption.AllDirectories).Any();

    private static string BuildMissingDatabaseWarning(string directory) =>
        $"No Watney quad database found at '{directory}'. Plate solves will fail until one is installed. " +
        "Run the installer again and choose the packs for your field, or download the core packs (D13) and extract each one -- including its .qdbindex sidecar, " +
        "which Watney requires alongside the .qdb files -- into that directory:" +
        Environment.NewLine + string.Join(Environment.NewLine, CorePackUrls) +
        Environment.NewLine + "Download-QuadDatabase.ps1, beside the application, does this for you. " +
        "Or set the FPA_QUADDB_DIR environment variable to point at an existing database.";
}
