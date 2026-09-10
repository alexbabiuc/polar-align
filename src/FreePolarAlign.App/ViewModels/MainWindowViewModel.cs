using FreePolarAlign.Core.Engine;

namespace FreePolarAlign.App.ViewModels;

/// <summary>
/// The application's one view model. Wires <see cref="IAlignmentEngine"/>'s
/// command/event boundary (D6) to bindable properties: buttons call the
/// <c>*Command</c> properties, which only ever call <see cref="IAlignmentEngine.SendAsync"/>;
/// every displayed property is derived from <see cref="UiState"/>, which is
/// itself derived only from the subscribed event stream via
/// <see cref="EngineEventReducer"/>. There is deliberately no code path here
/// that reads anything off the engine or the session directly -- that would
/// defeat the point of D6's message-shaped boundary.
///
/// This class itself is not unit-tested (it needs a UI thread marshaller and
/// wires up commands), but the two things it delegates to for anything that
/// could get the safety story wrong -- <see cref="EngineEventReducer"/> and
/// <see cref="AlignmentFormatting"/> -- are, thoroughly.
/// </summary>
public sealed class MainWindowViewModel : ViewModelBase, IDisposable
{
    private readonly IAlignmentEngine? _engine;
    private readonly Action<Action> _postToUiThread;
    private readonly IDisposable? _subscription;
    private UiState _state;

    public MainWindowViewModel(
        IAlignmentEngine? engine,
        string? startupWarning,
        SessionConfiguration defaultConfiguration,
        Action<Action>? postToUiThread = null)
    {
        _engine = engine;
        _postToUiThread = postToUiThread ?? (action => action());
        DefaultConfiguration = defaultConfiguration;
        StartupWarning = startupWarning;

        _state = UiState.Initial(defaultConfiguration.SiteLatitudeDegrees);
        if (engine is null)
        {
            _state = _state with { StatusMessage = "No engine available. See the warning above." };
        }
        else if (startupWarning is not null)
        {
            _state = _state with { StatusMessage = "Ready, but see the warning above before starting." };
        }

        StartCommand = new RelayCommand(
            () => SendAsync(new StartSessionCommand(DefaultConfiguration)),
            () => _engine is not null && !State.SessionActive);

        CaptureNextCommand = new RelayCommand(
            () => SendAsync(new CaptureNextPointCommand()),
            () => _engine is not null && State.SessionActive);

        CancelCommand = new RelayCommand(
            () => SendAsync(new CancelSessionCommand()),
            () => _engine is not null && State.SessionActive);

        AbortCommand = new RelayCommand(
            () => SendAsync(new AbortSessionCommand("Aborted by operator.")),
            () => _engine is not null && State.SessionActive);

        _subscription = _engine?.Events.Subscribe(new DelegateObserver<EngineEvent>(OnEngineEvent));
    }

    /// <summary>
    /// Null exactly when startup could not find a usable quad database (see
    /// <see cref="Services.EngineFactory"/>). Shown persistently rather than
    /// folded into <see cref="UiState.StatusMessage"/>, because it describes a
    /// standing condition of the install, not a transient session event.
    /// </summary>
    public string? StartupWarning { get; }

    public SessionConfiguration DefaultConfiguration { get; }

    public RelayCommand StartCommand { get; }

    public RelayCommand CaptureNextCommand { get; }

    public RelayCommand CancelCommand { get; }

    public RelayCommand AbortCommand { get; }

    public UiState State
    {
        get => _state;
        private set
        {
            if (SetField(ref _state, value))
            {
                RaiseDerivedPropertiesChanged();
            }
        }
    }

    public bool IsSessionActive => State.SessionActive;

    public bool IsAwaitingManualAction => State.AwaitingManualAction;

    public string StatusMessage => State.StatusMessage;

    public bool HasCurrentEstimate => State.CurrentEstimate is not null;

    public bool IsGoodEnoughToStop => State.CurrentEstimate is { } estimate && AlignmentFormatting.IsGoodEnoughToStop(estimate);

    public string AltitudeErrorText => State.CurrentEstimate is { } e
        ? AlignmentFormatting.FormatSignedWithSigma(e.AltitudeErrorArcminutes, e.AltitudeSigmaArcminutes)
        : "--";

    public string AzimuthErrorText => State.CurrentEstimate is { } e
        ? AlignmentFormatting.FormatSignedWithSigma(e.AzimuthErrorArcminutes, e.AzimuthSigmaArcminutes)
        : "--";

    public string TotalErrorText => State.CurrentEstimate is { } e
        ? AlignmentFormatting.FormatMagnitudeWithSigma(e.TotalErrorArcminutes, e.TotalSigmaArcminutes)
        : "--";

    public string ResidualRmsText => State.CurrentEstimate is { } e
        ? AlignmentFormatting.FormatResidualRms(e.ResidualRmsArcseconds)
        : "--";

    public string HemisphereText => AlignmentFormatting.Hemisphere(State.SiteLatitudeDegrees);

    public string ProgressText => $"{State.CapturedPointCount} / {(State.PlannedPointCount > 0 ? State.PlannedPointCount.ToString() : "?")} points captured";

    public bool HasWithheldReason => State.WithheldReason is not null;

    public string? WithheldReason => State.WithheldReason;

    public bool HasFaultReason => State.FaultReason is not null;

    public string? FaultReason => State.FaultReason;

    public bool HasCaptureWarning => State.CaptureWarning is not null;

    public string? CaptureWarning => State.CaptureWarning;

    public bool HasManualInstruction => State.ManualInstruction is not null;

    public string? ManualInstruction => State.ManualInstruction;

    public IReadOnlyList<string> Log => State.Log;

    private void OnEngineEvent(EngineEvent engineEvent)
    {
        UiState next = EngineEventReducer.Apply(_state, engineEvent);
        _postToUiThread(() => State = next);
    }

    private async Task SendAsync(EngineCommand command)
    {
        if (_engine is null)
        {
            return;
        }

        await _engine.SendAsync(command).ConfigureAwait(true);
    }

    private void RaiseDerivedPropertiesChanged()
    {
        OnPropertyChanged(nameof(IsSessionActive));
        OnPropertyChanged(nameof(IsAwaitingManualAction));
        OnPropertyChanged(nameof(StatusMessage));
        OnPropertyChanged(nameof(HasCurrentEstimate));
        OnPropertyChanged(nameof(IsGoodEnoughToStop));
        OnPropertyChanged(nameof(AltitudeErrorText));
        OnPropertyChanged(nameof(AzimuthErrorText));
        OnPropertyChanged(nameof(TotalErrorText));
        OnPropertyChanged(nameof(ResidualRmsText));
        OnPropertyChanged(nameof(HemisphereText));
        OnPropertyChanged(nameof(ProgressText));
        OnPropertyChanged(nameof(HasWithheldReason));
        OnPropertyChanged(nameof(WithheldReason));
        OnPropertyChanged(nameof(HasFaultReason));
        OnPropertyChanged(nameof(FaultReason));
        OnPropertyChanged(nameof(HasCaptureWarning));
        OnPropertyChanged(nameof(CaptureWarning));
        OnPropertyChanged(nameof(HasManualInstruction));
        OnPropertyChanged(nameof(ManualInstruction));
        OnPropertyChanged(nameof(Log));

        StartCommand.RaiseCanExecuteChanged();
        CaptureNextCommand.RaiseCanExecuteChanged();
        CancelCommand.RaiseCanExecuteChanged();
        AbortCommand.RaiseCanExecuteChanged();
    }

    public void Dispose() => _subscription?.Dispose();
}
