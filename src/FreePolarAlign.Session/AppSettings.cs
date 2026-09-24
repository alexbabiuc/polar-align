using System.Text.Json;
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
/// What the application remembers between runs.
///
/// Kept deliberately small, and deliberately not including anything the software
/// can measure for itself. The focal length is here because it is the one figure
/// worth carrying forward -- measured once from a solve, it turns every later
/// blind search into a bounded one -- and the site is here because typing a
/// latitude accurately is tedious enough that a user asked to do it nightly will
/// eventually stop being careful, which is the failure this file exists to
/// prevent.
///
/// Storing it is not the same as trusting it: see <see cref="SiteNeedsConfirmation"/>.
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

    /// <summary>
    /// Always true. Reloading the site is a convenience for the person typing;
    /// it is not evidence that the telescope is still where it was last night,
    /// and a stored latitude that silently follows a user to a different site
    /// would bias every altitude figure the software reports by the difference
    /// (D19). So a stored site is offered as a prefilled suggestion and takes
    /// effect only once explicitly confirmed.
    ///
    /// Expressed as a property rather than a comment so the intent is visible at
    /// the call site.
    /// </summary>
    [JsonIgnore]
    public bool SiteNeedsConfirmation => true;
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

            return settings is null
                ? new SettingsLoadResult(AppSettings.Empty, $"'{Location}' held no settings; starting from defaults.")
                : new SettingsLoadResult(Validate(settings, out string? warning), warning);
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
