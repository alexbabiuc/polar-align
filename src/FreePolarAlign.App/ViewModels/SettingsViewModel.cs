using System.Globalization;
using FreePolarAlign.Session;

namespace FreePolarAlign.App.ViewModels;

/// <summary>One entry in a solver picker.</summary>
public sealed record SolverOption(SolverKind Kind, string Label);

/// <summary>
/// The settings window's state. It edits a copy and hands the result back only
/// on Save, so closing the window any other way leaves what is remembered, and
/// what the main window is using, exactly as it was.
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
    private string? _error;

    /// <param name="defaultCapturePoints">What an unset capture-point count means, shown as the starting text.</param>
    /// <param name="save">Receives the settings to remember. Called only when every field is valid.</param>
    /// <param name="close">Closes the window.</param>
    public SettingsViewModel(AppSettings current, int defaultCapturePoints, Action<AppSettings> save, Action close)
    {
        _original = current ?? throw new ArgumentNullException(nameof(current));
        _save = save ?? throw new ArgumentNullException(nameof(save));
        _close = close ?? throw new ArgumentNullException(nameof(close));

        _capturePointsText = (current.DefaultCapturePoints ?? defaultCapturePoints).ToString(CultureInfo.InvariantCulture);
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

    public string CapturePointsText
    {
        get => _capturePointsText;
        set => SetField(ref _capturePointsText, value);
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

        Error = null;
        _save(_original with
        {
            DefaultCapturePoints = points,
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
        double.TryParse(text.Trim().Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out double seconds) &&
        AppSettings.IsValidSolveTimeout(seconds)
            ? seconds
            : null;

    private static string? NullIfBlank(string text) =>
        string.IsNullOrWhiteSpace(text) ? null : text.Trim();
}
