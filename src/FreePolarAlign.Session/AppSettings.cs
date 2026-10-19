using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace FreePolarAlign.Session;

/// <summary>
/// An observing site as it is stored between sessions.
/// </summary>
/// <param name="HeightMeters">
/// Height above the ellipsoid, which the user thinks of as altitude. It matters
/// far less than the other two -- it enters only through refraction and the tiny
/// diurnal-aberration and parallax terms -- so a value good to a few tens of
/// metres is ample, unlike the latitude.
/// </param>
public sealed record StoredSite(
    double LatitudeDegrees,
    double LongitudeDegrees,
    double HeightMeters);

/// <summary>
/// What is remembered about one particular camera.
///
/// Per camera, because both settings mean different things on different
/// cameras. A readout index is a position in one camera's own list -- index 1
/// is 16-bit on a ZWO and may be something else entirely on an ASCOM driver --
/// and a gain chosen for a sensitive guide camera would over-expose on another.
/// </summary>
/// <param name="ReadoutModeIndex">The readout mode chosen, by index into that camera's list.</param>
/// <param name="GainPercent">
/// The gain chosen, as a percentage of that camera's range (see
/// <c>GainScale</c>). Null for a camera whose gain is not set from here.
/// </param>
public sealed record CameraSettings(int? ReadoutModeIndex = null, int? GainPercent = null);

/// <summary>
/// Which plate solver does a job. Only the chosen one is used (D31). ASPS and
/// PS3 exist only for Windows.
/// </summary>
public enum SolverKind
{
    /// <summary>The built-in Watney solver.</summary>
    Internal,
    Asps,
    Astap,
    Ps3,
}

/// <summary>
/// What the application remembers between runs.
///
/// Kept deliberately small, and deliberately not including anything the software
/// can measure for itself. The focal length is here because it is the one figure
/// worth carrying forward -- measured once from a solve, it turns every later
/// blind search into a bounded one -- and the site is here because typing a
/// latitude accurately is tedious enough that a user asked to do it nightly will
/// eventually stop being careful, which is the failure this file exists to
/// prevent. A stored site is applied at startup without asking (D19 as
/// revised).
/// </summary>
public sealed record AppSettings
{
    public StoredSite? Site { get; init; }

    /// <summary>Best known focal length in millimetres, or null if never established.</summary>
    public double? FocalLengthMillimetres { get; init; }

    /// <summary>
    /// True when <see cref="FocalLengthMillimetres"/> came from a solve rather
    /// than from the user. Persisted because it decides how tight a scale hint
    /// the solver may be given, and a measured value carried into the next
    /// session is exactly the saving this file is for.
    /// </summary>
    public bool IsFocalLengthSolved { get; init; }

    /// <summary>
    /// The camera readout mode last chosen, by index, from before settings were
    /// kept per camera. Read so an existing choice is not lost -- it is applied
    /// to the camera it was chosen for, once, and then kept in
    /// <see cref="Cameras"/> -- and no longer written.
    /// </summary>
    public int? ReadoutModeIndex { get; init; }

    /// <summary>
    /// Settings remembered per camera, keyed by <see cref="CameraKey"/>.
    ///
    /// The readout mode is remembered because it is a deliberate trade the user
    /// made -- speed against bit depth -- and having it silently revert to the
    /// driver's default between sessions would change the data without changing
    /// anything on screen. The gain for the same reason, and because it was the
    /// setting that ruined a night when nobody could see it.
    /// </summary>
    public IReadOnlyDictionary<string, CameraSettings>? Cameras { get; init; }

    /// <summary>
    /// The exposure last chosen, in seconds. Remembered for the same reason as
    /// the readout mode: it is a judgement about the sky and the optics that
    /// does not change between one night and the next, and re-making it every
    /// session is how a frame ends up over-exposed again.
    /// </summary>
    public double? ExposureSeconds { get; init; }

    /// <summary>
    /// The number of capture points a sequence takes, or null for the built-in
    /// default. Set in the settings window, which is the only place it is
    /// edited (D34).
    /// </summary>
    public int? DefaultCapturePoints { get; init; }

    /// <summary>
    /// The sweep a sequence asks for, in degrees, or null for the built-in
    /// default. A request: the planner may shrink it.
    /// </summary>
    public double? SweepDegrees { get; init; }

    public static bool IsValidSweep(double degrees) => double.IsFinite(degrees) && degrees > 0.0;

    /// <summary>
    /// The wait after a slew ends before the sample's frame is exposed, in
    /// seconds, or null for the engine's built-in two seconds (D26).
    /// </summary>
    public double? SettleSeconds { get; init; }

    /// <summary>
    /// The range the settle time is kept in. Zero is a real choice: a light,
    /// stiff mount can be still the moment it stops, and waiting then only
    /// slows every sample. A minute is far past any ringing worth waiting out,
    /// and a value beyond it is far likelier a typo -- an extra digit -- than
    /// a mount that needs it, at a cost paid on every sample.
    /// </summary>
    public const double MaximumSettleSeconds = 60.0;

    [JsonIgnore]
    public TimeSpan? SettleDelay => SettleSeconds is { } seconds ? TimeSpan.FromSeconds(seconds) : null;

    public static bool IsValidSettle(double seconds) =>
        double.IsFinite(seconds) && seconds >= 0.0 && seconds <= MaximumSettleSeconds;

    /// <summary>
    /// The solver for every frame, blind and near alike, or null for
    /// <see cref="SolverKind.Internal"/>. One choice and not one per job:
    /// solvers give slightly different answers for the same frame, and a
    /// reading built from two of them moves with the solver rather than the
    /// mount (D42).
    /// </summary>
    public SolverKind? Solver { get; init; }

    /// <summary>
    /// How long a blind solve may take, in seconds, or null for
    /// <see cref="DefaultBlindSolveTimeoutSeconds"/>. Separate from the near
    /// one because a blind search covers the whole sky and a near one a few
    /// degrees, and a single limit would be too short for one or too long for
    /// the other.
    /// </summary>
    public double? BlindSolveTimeoutSeconds { get; init; }

    /// <summary>How long a near solve may take, in seconds, or null for <see cref="DefaultNearSolveTimeoutSeconds"/>.</summary>
    public double? NearSolveTimeoutSeconds { get; init; }

    public const double DefaultBlindSolveTimeoutSeconds = 20.0;

    public const double DefaultNearSolveTimeoutSeconds = 5.0;

    /// <summary>
    /// The range a solve timeout is kept in. A second is the least any solver
    /// here needs to start; ten minutes is N.I.N.A.'s limit for the same
    /// command-line solvers, and anything longer is a frame nobody is waiting
    /// for.
    /// </summary>
    public const double MinimumSolveTimeoutSeconds = 1.0;

    public const double MaximumSolveTimeoutSeconds = 600.0;

    [JsonIgnore]
    public TimeSpan BlindSolveTimeout => TimeSpan.FromSeconds(BlindSolveTimeoutSeconds ?? DefaultBlindSolveTimeoutSeconds);

    [JsonIgnore]
    public TimeSpan NearSolveTimeout => TimeSpan.FromSeconds(NearSolveTimeoutSeconds ?? DefaultNearSolveTimeoutSeconds);

    public static bool IsValidSolveTimeout(double seconds) =>
        double.IsFinite(seconds) && seconds >= MinimumSolveTimeoutSeconds && seconds <= MaximumSolveTimeoutSeconds;

    /// <summary>Where the ASPS executable is, or null if not set.</summary>
    public string? AspsPath { get; init; }

    /// <summary>Where the ASTAP executable is, or null if not set.</summary>
    public string? AstapPath { get; init; }

    /// <summary>Where the PS3 executable is, or null if not set.</summary>
    public string? Ps3Path { get; init; }

    public string? CameraProviderName { get; init; }

    public string? CameraDeviceId { get; init; }

    public string? MountProviderName { get; init; }

    public string? MountDeviceId { get; init; }

    public static AppSettings Empty { get; } = new();

    /// <summary>
    /// The key a camera's settings are stored under: its provider and its own
    /// identity -- a serial number when the camera has one -- or failing that the
    /// id the provider enumerated it under.
    ///
    /// The provider is part of it because the same camera reached through ASCOM
    /// and through its vendor's SDK is two different things here: different
    /// readout lists, and gain controlled in one and not the other.
    /// </summary>
    public static string CameraKey(string providerName, string? uniqueId, string deviceId) =>
        $"{providerName}/{(string.IsNullOrEmpty(uniqueId) ? deviceId : uniqueId)}";

    public CameraSettings? ForCamera(string key) =>
        Cameras is not null && Cameras.TryGetValue(key, out CameraSettings? settings) ? settings : null;

    /// <summary>
    /// A copy with one camera's settings changed, or this same instance when the
    /// change changes nothing -- so the caller's "did anything change" check,
    /// which compares instances, still works across a dictionary it would
    /// otherwise always see as new.
    /// </summary>
    public AppSettings WithCamera(string key, Func<CameraSettings, CameraSettings> update)
    {
        ArgumentNullException.ThrowIfNull(update);

        CameraSettings? existing = ForCamera(key);
        CameraSettings next = update(existing ?? new CameraSettings());
        if (existing is not null && existing == next)
        {
            return this;
        }

        var cameras = Cameras is null
            ? new Dictionary<string, CameraSettings>(StringComparer.Ordinal)
            : new Dictionary<string, CameraSettings>(Cameras, StringComparer.Ordinal);
        cameras[key] = next;
        return this with { Cameras = cameras };
    }
}

/// <param name="Warning">
/// Non-null when the file existed but could not be used. The settings returned
/// alongside it are the empty defaults -- a corrupt file must not stop the
/// application starting, but it must not be silently replaced either, because
/// the user is about to be asked to retype something they will believe is
/// already stored.
/// </param>
public sealed record SettingsLoadResult(AppSettings Settings, string? Warning);

/// <summary>
/// Where settings are read from and written to. An interface so tests can drive
/// the load-ask-store cycle without touching a real home directory.
/// </summary>
public interface ISettingsStore
{
    /// <summary>A description of where the settings live, for the UI and the log.</summary>
    string Location { get; }

    SettingsLoadResult Load();

    /// <summary>
    /// Persists settings. Failure to write is reported as a message rather than
    /// thrown: a read-only home directory is a reason to warn, not a reason to
    /// lose the session that is already running.
    /// </summary>
    string? Save(AppSettings settings);
}

/// <summary>
/// The default store: one small JSON file under the user's profile, written
/// whole.
///
/// Written via a temporary file and a move, so an interrupted write leaves the
/// previous settings intact rather than a truncated file. That matters more than
/// it looks: the write happens at the start of an observing session, and the
/// most likely interruption is the laptop being unplugged in the dark.
/// </summary>
public sealed class JsonFileSettingsStore : ISettingsStore
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter() },
    };

    public JsonFileSettingsStore(string? filePath = null)
    {
        Location = filePath ?? DefaultPath();
    }

    public string Location { get; }

    public static string DefaultPath() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        ".free-polar-align",
        "settings.json");

    public SettingsLoadResult Load()
    {
        if (!File.Exists(Location))
        {
            return new SettingsLoadResult(AppSettings.Empty, null);
        }

        try
        {
            string json = File.ReadAllText(Location);
            AppSettings? settings = JsonSerializer.Deserialize<AppSettings>(json, Options);
            if (settings is null)
            {
                return new SettingsLoadResult(AppSettings.Empty, $"'{Location}' held no settings; starting from defaults.");
            }

            AppSettings migrated = MigrateSolverChoice(settings, json, out string? migration);
            if (migrated != settings)
            {
                // Written back at once, so a choice dropped in the merge is
                // reported on one start and not on every one after. A failed
                // write only means the migration repeats next time.
                Save(migrated);
            }

            AppSettings valid = Validate(migrated, out string? warning);
            string? warnings = migration is null ? warning
                : warning is null ? migration
                : migration + " " + warning;
            return new SettingsLoadResult(valid, warnings);
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            return new SettingsLoadResult(
                AppSettings.Empty,
                $"Could not read settings from '{Location}' ({ex.Message}). Starting from defaults -- " +
                "the site and focal length will need entering again.");
        }
    }

    public string? Save(AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        try
        {
            string? directory = Path.GetDirectoryName(Location);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            string temporary = Location + ".tmp";
            File.WriteAllText(temporary, JsonSerializer.Serialize(settings, Options));
            File.Move(temporary, Location, overwrite: true);
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return $"Could not save settings to '{Location}': {ex.Message}. " +
                   "The session will run normally, but nothing will be remembered for next time.";
        }
    }

    /// <summary>
    /// Folds the separate blind and near solvers of 0.0.31 and earlier into the
    /// one solver (D42). They are read from the file's own text because the
    /// settings no longer carry them, and they are not written back.
    ///
    /// Where the two differed, the one that was not the built-in solver is
    /// kept, since it was chosen rather than left at the default, and the
    /// blind one where both were chosen. The user is told which.
    /// </summary>
    internal static AppSettings MigrateSolverChoice(AppSettings settings, string json, out string? warning)
    {
        warning = null;
        if (settings.Solver is not null)
        {
            return settings;
        }

        SolverKind? blind = null;
        SolverKind? near = null;
        try
        {
            if (JsonNode.Parse(json) is JsonObject root)
            {
                blind = ReadSolver(root, "blindSolver");
                near = ReadSolver(root, "nearSolver");
            }
        }
        catch (JsonException)
        {
            return settings;
        }

        if (blind is null && near is null)
        {
            return settings;
        }

        SolverKind blindKind = blind ?? SolverKind.Internal;
        SolverKind nearKind = near ?? SolverKind.Internal;
        SolverKind kept = blindKind != SolverKind.Internal ? blindKind : nearKind;

        if (blindKind != nearKind)
        {
            warning = $"Blind and near frames had different solvers ({blindKind} and {nearKind}). " +
                      $"One solver now does both, and {kept} was kept. It can be changed in Settings.";
        }

        return settings with { Solver = kept == SolverKind.Internal ? null : kept };
    }

    private static SolverKind? ReadSolver(JsonObject root, string name) =>
        root[name] is JsonValue value &&
        value.TryGetValue(out string? text) &&
        Enum.TryParse(text, ignoreCase: true, out SolverKind kind) &&
        Enum.IsDefined(kind)
            ? kind
            : null;

    /// <summary>
    /// Discards stored values that cannot be right. A latitude of 200 degrees or
    /// a negative focal length came from a hand-edited file or a different
    /// version's schema; carrying them forward would produce a confidently wrong
    /// answer, and dropping them costs only a retype.
    /// </summary>
    private static AppSettings Validate(AppSettings settings, out string? warning)
    {
        var complaints = new List<string>();
        AppSettings result = settings;

        if (result.Site is { } site)
        {
            bool latitudeBad = !double.IsFinite(site.LatitudeDegrees) || Math.Abs(site.LatitudeDegrees) > 90.0;
            bool longitudeBad = !double.IsFinite(site.LongitudeDegrees) || Math.Abs(site.LongitudeDegrees) > 180.0;
            bool heightBad = !double.IsFinite(site.HeightMeters) || site.HeightMeters is < -500.0 or > 9000.0;

            if (latitudeBad || longitudeBad || heightBad)
            {
                complaints.Add("the stored site was out of range and has been discarded");
                result = result with { Site = null };
            }
        }

        if (result.FocalLengthMillimetres is { } focalLength &&
            (!double.IsFinite(focalLength) || focalLength <= 0.0 || focalLength > 100_000.0))
        {
            complaints.Add("the stored focal length was out of range and has been discarded");
            result = result with { FocalLengthMillimetres = null, IsFocalLengthSolved = false };
        }

        if (result.DefaultCapturePoints is < 3)
        {
            complaints.Add("the stored default capture-point count was below the minimum of 3 and has been discarded");
            result = result with { DefaultCapturePoints = null };
        }

        if (result.SweepDegrees is { } sweep && !AppSettings.IsValidSweep(sweep))
        {
            complaints.Add("the stored sweep was not a positive number of degrees and has been discarded");
            result = result with { SweepDegrees = null };
        }

        if (result.SettleSeconds is { } settle && !AppSettings.IsValidSettle(settle))
        {
            complaints.Add(FormattableString.Invariant(
                $"the stored settle time was not between 0 and {AppSettings.MaximumSettleSeconds:G} seconds and has been discarded"));
            result = result with { SettleSeconds = null };
        }

        if (result.Solver is { } solver && !Enum.IsDefined(solver))
        {
            complaints.Add("the stored solver was not one this version knows and has been discarded");
            result = result with { Solver = null };
        }

        if (result.BlindSolveTimeoutSeconds is { } blindTimeout && !AppSettings.IsValidSolveTimeout(blindTimeout))
        {
            complaints.Add("the stored blind solve timeout was out of range and has been discarded");
            result = result with { BlindSolveTimeoutSeconds = null };
        }

        if (result.NearSolveTimeoutSeconds is { } nearTimeout && !AppSettings.IsValidSolveTimeout(nearTimeout))
        {
            complaints.Add("the stored near solve timeout was out of range and has been discarded");
            result = result with { NearSolveTimeoutSeconds = null };
        }

        if (result.Cameras is { Count: > 0 } cameras)
        {
            bool corrected = false;
            var kept = new Dictionary<string, CameraSettings>(StringComparer.Ordinal);

            foreach ((string key, CameraSettings camera) in cameras)
            {
                CameraSettings clean = camera with
                {
                    ReadoutModeIndex = camera.ReadoutModeIndex is >= 0 ? camera.ReadoutModeIndex : null,
                    GainPercent = camera.GainPercent is >= 0 and <= 100 ? camera.GainPercent : null,
                };

                corrected |= clean != camera;
                kept[key] = clean;
            }

            if (corrected)
            {
                complaints.Add("a stored camera setting was out of range and has been discarded");
                result = result with { Cameras = kept };
            }
        }

        warning = complaints.Count == 0
            ? null
            : $"Settings loaded with corrections: {string.Join("; ", complaints)}.";
        return result;
    }
}
