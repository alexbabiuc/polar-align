using System.Globalization;
using FreePolarAlign.Core.Engine;
using FreePolarAlign.Session;

namespace FreePolarAlign.App.ViewModels;

/// <summary>One entry in a solver picker.</summary>
public sealed record SolverOption(SolverKind Kind, string Label);

/// <summary>
/// The settings window's state. It edits a copy and hands the result back only
/// on Save, so closing the window any other way leaves what is remembered, and
/// what the main window is using, exactly as it was. Saving is also what
/// applies the site and the focal length; there is no separate confirmation
/// (D19 as revised).
/// </summary>
public sealed class SettingsViewModel : ViewModelBase
{
    /// <summary>The smallest sequence the engine accepts (D7).</summary>
    internal const int MinimumCapturePoints = 3;

    public static IReadOnlyList<SolverOption> SolverOptions { get; } = new[]
    {
        new SolverOption(SolverKind.Internal, "Internal (Watney)"),
        new SolverOption(SolverKind.Asps, "ASPS"),
        new SolverOption(SolverKind.Astap, "ASTAP"),
        new SolverOption(SolverKind.Ps3, "PS3"),
    };

    private readonly AppSettings _original;
    private readonly Action<AppSettings> _save;
    private readonly Action _close;

    private string _capturePointsText;
    private SolverOption _blindSolver;
    private SolverOption _nearSolver;
    private string _blindTimeoutText;
    private string _nearTimeoutText;
    private string _aspsPath;
    private string _astapPath;
    private string _ps3Path;
    private string _sweepText;
    private string _latitudeText;
    private string _longitudeText;
    private string _heightText;
    private string _focalLengthText;
    private string? _error;

    /// <param name="defaults">What an unset capture-point count or sweep means, shown as the starting text.</param>
    /// <param name="save">Receives the settings to remember. Called only when every field is valid.</param>
    /// <param name="close">Closes the window.</param>
    /// <param name="canChangeSite">
    /// False while a sequence runs: every capture already taken was reduced with
    /// the site in use, so the engine refuses a new one until it ends.
    /// </param>
    public SettingsViewModel(
        AppSettings current,
        SessionConfiguration defaults,
        Action<AppSettings> save,
        Action close,
        bool canChangeSite = true)
    {
        _original = current ?? throw new ArgumentNullException(nameof(current));
        ArgumentNullException.ThrowIfNull(defaults);
        _save = save ?? throw new ArgumentNullException(nameof(save));
        _close = close ?? throw new ArgumentNullException(nameof(close));
        CanChangeSite = canChangeSite;

        _capturePointsText = (current.DefaultCapturePoints ?? defaults.CapturePoints).ToString(CultureInfo.InvariantCulture);
        _sweepText = (current.SweepDegrees ?? defaults.RequestedSweepDegrees).ToString("G", CultureInfo.InvariantCulture);
        _latitudeText = current.Site?.LatitudeDegrees.ToString("F5", CultureInfo.InvariantCulture) ?? string.Empty;
        _longitudeText = current.Site?.LongitudeDegrees.ToString("F5", CultureInfo.InvariantCulture) ?? string.Empty;
        _heightText = current.Site?.HeightMeters.ToString("F0", CultureInfo.InvariantCulture) ?? string.Empty;
        _focalLengthText = FormatFocalLength(current.FocalLengthMillimetres);
        _blindSolver = OptionFor(current.BlindSolver);
        _nearSolver = OptionFor(current.NearSolver);
        _blindTimeoutText = Seconds(current.BlindSolveTimeout);
        _nearTimeoutText = Seconds(current.NearSolveTimeout);
        _aspsPath = current.AspsPath ?? string.Empty;
        _astapPath = current.AstapPath ?? string.Empty;
        _ps3Path = current.Ps3Path ?? string.Empty;

        SaveCommand = new RelayCommand(() =>
        {
            Save();
            return Task.CompletedTask;
        });
        CancelCommand = new RelayCommand(() =>
        {
            _close();
            return Task.CompletedTask;
        });
    }

    public RelayCommand SaveCommand { get; }

    public RelayCommand CancelCommand { get; }

    public bool CanChangeSite { get; }

    public string LatitudeText
    {
        get => _latitudeText;
        set => SetField(ref _latitudeText, value);
    }

    public string LongitudeText
    {
        get => _longitudeText;
        set => SetField(ref _longitudeText, value);
    }

    public string HeightText
    {
        get => _heightText;
        set => SetField(ref _heightText, value);
    }

    public string FocalLengthText
    {
        get => _focalLengthText;
        set => SetField(ref _focalLengthText, value);
    }

    /// <summary>
    /// Said only of a figure a solve measured, since that one is better than
    /// anything typed and is worth leaving alone.
    /// </summary>
    public bool IsFocalLengthMeasured => _original.IsFocalLengthSolved && _original.FocalLengthMillimetres is not null;

    public string CapturePointsText
    {
        get => _capturePointsText;
        set => SetField(ref _capturePointsText, value);
    }

    /// <summary>The sweep, in degrees.</summary>
    public string SweepText
    {
        get => _sweepText;
        set => SetField(ref _sweepText, value);
    }

    public SolverOption BlindSolver
    {
        get => _blindSolver;
        set => SetField(ref _blindSolver, value);
    }

    public SolverOption NearSolver
    {
        get => _nearSolver;
        set => SetField(ref _nearSolver, value);
    }

    /// <summary>The blind solve timeout, in seconds.</summary>
    public string BlindTimeoutText
    {
        get => _blindTimeoutText;
        set => SetField(ref _blindTimeoutText, value);
    }

    /// <summary>The near solve timeout, in seconds.</summary>
    public string NearTimeoutText
    {
        get => _nearTimeoutText;
        set => SetField(ref _nearTimeoutText, value);
    }

    public string AspsPath
    {
        get => _aspsPath;
        set => SetField(ref _aspsPath, value);
    }

    public string AstapPath
    {
        get => _astapPath;
        set => SetField(ref _astapPath, value);
    }

    public string Ps3Path
    {
        get => _ps3Path;
        set => SetField(ref _ps3Path, value);
    }

    public string? Error
    {
        get => _error;
        private set => SetField(ref _error, value);
    }

    private void Save()
    {
        if (!int.TryParse(CapturePointsText, NumberStyles.Integer, CultureInfo.InvariantCulture, out int points) ||
            points < MinimumCapturePoints)
        {
            Error = $"A sequence needs at least {MinimumCapturePoints} capture points (D7).";
            return;
        }

        if (ParseTimeout(BlindTimeoutText) is not { } blindTimeout || ParseTimeout(NearTimeoutText) is not { } nearTimeout)
        {
            Error = FormattableString.Invariant(
                $"A solve timeout must be between {AppSettings.MinimumSolveTimeoutSeconds:G} and {AppSettings.MaximumSolveTimeoutSeconds:G} seconds.");
            return;
        }

        if (!TryParseDecimal(SweepText, out double sweep) || !AppSettings.IsValidSweep(sweep))
        {
            Error = "The sweep must be a positive number of degrees.";
            return;
        }

        if (!TryParseSite(out StoredSite? site, out string? siteProblem))
        {
            Error = siteProblem;
            return;
        }

        if (!TryParseFocalLength(out double? focalLength))
        {
            Error = "Focal length must be a positive number of millimetres, or empty for fully blind solves.";
            return;
        }

        // An unedited figure keeps its provenance: retyping nothing must not
        // demote a measured focal length to an entered one.
        bool focalLengthUnchanged = FocalLengthText.Trim() == FormatFocalLength(_original.FocalLengthMillimetres);

        Error = null;
        _save(_original with
        {
            DefaultCapturePoints = points,
            SweepDegrees = sweep,
            Site = CanChangeSite ? site : _original.Site,
            FocalLengthMillimetres = focalLengthUnchanged ? _original.FocalLengthMillimetres : focalLength,
            IsFocalLengthSolved = focalLengthUnchanged && _original.IsFocalLengthSolved,
            BlindSolver = BlindSolver.Kind,
            NearSolver = NearSolver.Kind,
            BlindSolveTimeoutSeconds = blindTimeout,
            NearSolveTimeoutSeconds = nearTimeout,
            AspsPath = NullIfBlank(AspsPath),
            AstapPath = NullIfBlank(AstapPath),
            Ps3Path = NullIfBlank(Ps3Path),
        });
        _close();
    }

    private static SolverOption OptionFor(SolverKind? kind) =>
        SolverOptions.First(option => option.Kind == (kind ?? SolverKind.Internal));

    private static string Seconds(TimeSpan timeout) =>
        timeout.TotalSeconds.ToString("G", CultureInfo.InvariantCulture);

    /// <summary>
    /// Seconds as typed, with a decimal point or a decimal comma: on a
    /// decimal-comma locale "2,5" is how the number is written, and a field
    /// that rejects it for want of a point is a field that looks broken.
    /// </summary>
    private static double? ParseTimeout(string text) =>
        TryParseDecimal(text, out double seconds) && AppSettings.IsValidSolveTimeout(seconds)
            ? seconds
            : null;

    /// <summary>
    /// All three empty is no site yet, which is allowed: a session simply cannot
    /// start until there is one. Height on its own may be left empty, since it
    /// enters only through refraction and some very small terms, and is then
    /// taken as sea level.
    /// </summary>
    private bool TryParseSite(out StoredSite? site, out string? problem)
    {
        site = null;
        problem = null;
        if (string.IsNullOrWhiteSpace(LatitudeText) && string.IsNullOrWhiteSpace(LongitudeText) &&
            string.IsNullOrWhiteSpace(HeightText))
        {
            return true;
        }

        if (!TryParseDecimal(LatitudeText, out double latitude) || !double.IsFinite(latitude) || Math.Abs(latitude) > 90.0)
        {
            problem = "Latitude must be a number of degrees between -90 and 90. North is positive.";
            return false;
        }

        if (!TryParseDecimal(LongitudeText, out double longitude) || !double.IsFinite(longitude) || Math.Abs(longitude) > 180.0)
        {
            problem = "Longitude must be a number of degrees between -180 and 180. East is positive.";
            return false;
        }

        double height = 0.0;
        if (!string.IsNullOrWhiteSpace(HeightText) && (!TryParseDecimal(HeightText, out height) || !double.IsFinite(height)))
        {
            problem = "Altitude must be a number of metres, or empty for sea level.";
            return false;
        }

        site = new StoredSite(latitude, longitude, height);
        return true;
    }

    private bool TryParseFocalLength(out double? focalLength)
    {
        focalLength = null;
        if (string.IsNullOrWhiteSpace(FocalLengthText))
        {
            return true;
        }

        if (!TryParseDecimal(FocalLengthText, out double value) || !double.IsFinite(value) || value <= 0.0)
        {
            return false;
        }

        focalLength = value;
        return true;
    }

    private static string FormatFocalLength(double? millimetres) =>
        millimetres?.ToString("F1", CultureInfo.InvariantCulture) ?? string.Empty;

    private static bool TryParseDecimal(string text, out double value) =>
        double.TryParse(text.Trim().Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out value);

    private static string? NullIfBlank(string text) =>
        string.IsNullOrWhiteSpace(text) ? null : text.Trim();
}
