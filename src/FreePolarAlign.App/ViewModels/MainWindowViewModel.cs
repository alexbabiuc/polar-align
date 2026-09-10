using System.Globalization;
using FreePolarAlign.App.Services;
using FreePolarAlign.Core.Engine;
using FreePolarAlign.Devices;
using FreePolarAlign.Session;

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
/// The editable fields -- site, focal length, and the proposed coordinates --
/// are the exception to "derived from state", and deliberately so: text a user
/// is part-way through typing is not engine state, and a field that snapped back
/// to the engine's value on every event would be unusable. They are seeded from
/// state, then owned by the user until submitted.
///
/// This class itself is not unit-tested (it needs a UI thread marshaller and
/// wires up commands), but everything it delegates to for anything that could
/// get the safety story wrong -- <see cref="EngineEventReducer"/>,
/// <see cref="AlignmentFormatting"/> and <see cref="CoordinateText"/> -- is,
/// thoroughly.
/// </summary>
public sealed class MainWindowViewModel : ViewModelBase, IDisposable
{
    private readonly IAlignmentEngine? _engine;
    private readonly DeviceCatalog? _catalog;
    private readonly ISettingsStore? _settingsStore;
    private readonly SessionLog? _log;
    private readonly Action<Action> _postToUiThread;
    private readonly IDisposable? _subscription;

    private UiState _state;
    private AppSettings _settings;

    private DeviceOption? _selectedCamera;
    private DeviceOption? _selectedMount;
    private string _latitudeText = string.Empty;
    private string _longitudeText = string.Empty;
    private string _heightText = string.Empty;
    private string _focalLengthText = string.Empty;
    private string _capturePointsText;
    private string _sweepText;
    private string _proposalRaText = string.Empty;
    private string _proposalDecText = string.Empty;
    private string? _entryError;

    public MainWindowViewModel(
        IAlignmentEngine? engine,
        DeviceCatalog? catalog,
        ISettingsStore? settingsStore,
        AppSettings? settings,
        SessionLog? log,
        IReadOnlyList<string>? warnings,
        SessionConfiguration defaultConfiguration,
        Action<Action>? postToUiThread = null)
    {
        _engine = engine;
        _catalog = catalog;
        _settingsStore = settingsStore;
        _log = log;
        _settings = settings ?? AppSettings.Empty;
        _postToUiThread = postToUiThread ?? (action => action());

        Warnings = warnings ?? Array.Empty<string>();
        DefaultConfiguration = defaultConfiguration;
        LogPath = log?.Path;

        CameraOptions = catalog?.Cameras ?? Array.Empty<DeviceOption>();
        MountOptions = catalog?.Mounts ?? Array.Empty<DeviceOption>();

        // A remembered device is preselected but not opened. Connecting on
        // startup would move nothing, but it would also talk to hardware the
        // user has not yet said is the hardware they are using tonight.
        _selectedCamera = catalog?.Find(DeviceRole.Camera, _settings.CameraProviderName, _settings.CameraDeviceId)
                          ?? CameraOptions.FirstOrDefault();
        _selectedMount = catalog?.Find(DeviceRole.Mount, _settings.MountProviderName, _settings.MountDeviceId)
                         ?? MountOptions.FirstOrDefault();

        _capturePointsText = defaultConfiguration.CapturePoints.ToString(CultureInfo.InvariantCulture);
        _sweepText = defaultConfiguration.RequestedSweepDegrees.ToString("F0", CultureInfo.InvariantCulture);

        // The stored site prefills the fields and nothing more. It takes effect
        // only when the user presses the button, because a latitude that
        // silently follows someone to a new site biases every altitude figure
        // the software reports by the difference (D19).
        if (_settings.Site is { } site)
        {
            _latitudeText = site.LatitudeDegrees.ToString("F5", CultureInfo.InvariantCulture);
            _longitudeText = site.LongitudeDegrees.ToString("F5", CultureInfo.InvariantCulture);
            _heightText = site.HeightMeters.ToString("F0", CultureInfo.InvariantCulture);
        }

        if (_settings.FocalLengthMillimetres is { } focalLength)
        {
            _focalLengthText = focalLength.ToString("F1", CultureInfo.InvariantCulture);
        }

        _state = UiState.Initial();
        if (engine is null)
        {
            _state = _state with { StatusMessage = "No engine available. See the warnings above." };
        }

        ConnectCameraCommand = new RelayCommand(
            () => ConnectAsync(DeviceKind.Camera, SelectedCamera),
            () => _engine is not null && SelectedCamera is not null && !State.Camera.IsConnected && !State.SessionActive);

        DisconnectCameraCommand = new RelayCommand(
            () => SendAsync(new DisconnectDeviceCommand(DeviceKind.Camera)),
            () => _engine is not null && State.Camera.IsConnected && !State.SessionActive);

        ConnectMountCommand = new RelayCommand(
            () => ConnectAsync(DeviceKind.Mount, SelectedMount),
            () => _engine is not null && SelectedMount is not null && !State.Mount.IsConnected && !State.SessionActive);

        DisconnectMountCommand = new RelayCommand(
            () => SendAsync(new DisconnectDeviceCommand(DeviceKind.Mount)),
            () => _engine is not null && State.Mount.IsConnected && !State.SessionActive);

        ConfirmSiteCommand = new RelayCommand(ConfirmSiteAsync, () => _engine is not null && !State.SessionActive);

        ApplyFocalLengthCommand = new RelayCommand(
            ApplyFocalLengthAsync,
            () => _engine is not null && State.Camera.IsConnected);

        StartCommand = new RelayCommand(
            StartAsync,
            () => _engine is not null && !State.SessionActive &&
                  State.Camera.IsConnected && State.Mount.IsConnected && State.IsSiteConfigured);

        ConfirmProposalCommand = new RelayCommand(
            ConfirmProposalAsync,
            () => _engine is not null && State.Proposal is not null);

        RestoreProposalCoordinatesCommand = new RelayCommand(
            () =>
            {
                SeedProposalCoordinates(State.Proposal);
                EntryError = null;
                return Task.CompletedTask;
            },
            () => State.Proposal is not null);

        CancelCommand = new RelayCommand(
            () => SendAsync(new CancelSessionCommand()),
            () => _engine is not null && State.SessionActive);

        AbortCommand = new RelayCommand(
            () => SendAsync(new AbortSessionCommand("Aborted by operator.")),
            () => _engine is not null && State.SessionActive);

        _subscription = _engine?.Events.Subscribe(new DelegateObserver<EngineEvent>(OnEngineEvent));
    }

    /// <summary>
    /// Standing conditions of the install, shown persistently rather than folded
    /// into the transient status line: a missing quad database (D13) or a plugin
    /// that would not load (D4) describes the installation, not a moment.
    /// </summary>
    public IReadOnlyList<string> Warnings { get; }

    public bool HasWarnings => Warnings.Count > 0;

    public string WarningText => string.Join(Environment.NewLine + Environment.NewLine, Warnings);

    public string? LogPath { get; }

    public SessionConfiguration DefaultConfiguration { get; }

    public IReadOnlyList<DeviceOption> CameraOptions { get; }

    public IReadOnlyList<DeviceOption> MountOptions { get; }

    public RelayCommand ConnectCameraCommand { get; }

    public RelayCommand DisconnectCameraCommand { get; }

    public RelayCommand ConnectMountCommand { get; }

    public RelayCommand DisconnectMountCommand { get; }

    public RelayCommand ConfirmSiteCommand { get; }

    public RelayCommand ApplyFocalLengthCommand { get; }

    public RelayCommand StartCommand { get; }

    public RelayCommand ConfirmProposalCommand { get; }

    public RelayCommand RestoreProposalCoordinatesCommand { get; }

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

    // ---- Editable entry ----

    public DeviceOption? SelectedCamera
    {
        get => _selectedCamera;
        set
        {
            if (SetField(ref _selectedCamera, value))
            {
                ConnectCameraCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public DeviceOption? SelectedMount
    {
        get => _selectedMount;
        set
        {
            if (SetField(ref _selectedMount, value))
            {
                ConnectMountCommand.RaiseCanExecuteChanged();
            }
        }
    }

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

    public string CapturePointsText
    {
        get => _capturePointsText;
        set => SetField(ref _capturePointsText, value);
    }

    public string SweepText
    {
        get => _sweepText;
        set => SetField(ref _sweepText, value);
    }

    /// <summary>
    /// The right ascension the mount will be sent to, editable. Seeded from the
    /// engine's proposal; whatever is in it when the button is pressed is what
    /// gets used (D18's override).
    /// </summary>
    public string ProposalRaText
    {
        get => _proposalRaText;
        set => SetField(ref _proposalRaText, value);
    }

    public string ProposalDecText
    {
        get => _proposalDecText;
        set => SetField(ref _proposalDecText, value);
    }

    /// <summary>
    /// Why the last thing typed could not be used. Kept separate from the
    /// engine's own refusals: this one never reached the engine.
    /// </summary>
    public string? EntryError
    {
        get => _entryError;
        private set
        {
            if (SetField(ref _entryError, value))
            {
                OnPropertyChanged(nameof(HasEntryError));
            }
        }
    }

    public bool HasEntryError => EntryError is not null;

    // ---- Derived display ----

    public bool IsSessionActive => State.SessionActive;

    public bool IsAwaitingManualAction => State.AwaitingManualAction;

    public string StatusMessage => State.StatusMessage;

    public bool IsCameraConnected => State.Camera.IsConnected;

    public bool IsMountConnected => State.Mount.IsConnected;

    public string CameraSummary => State.Camera.IsConnected
        ? $"{State.Camera.Name}  [{State.Camera.Driver}]"
        : State.Camera.Problem ?? "Not connected";

    public string CameraPixelText =>
        AlignmentFormatting.PixelSize(State.CameraPixelSizeMicrons, State.CameraWidthPixels, State.CameraHeightPixels);

    public string MountSummary => State.Mount.IsConnected
        ? $"{State.Mount.Name}  [{State.Mount.Driver}]"
        : State.Mount.Problem ?? "Not connected";

    public string MountCoordinatesText =>
        AlignmentFormatting.Coordinates(State.MountRaDegrees, State.MountDecDegrees);

    public string TrackingText => AlignmentFormatting.Tracking(State.MountTracking);

    public bool IsTracking => State.MountTracking == MountTrackingState.Tracking;

    public string PierSideText => AlignmentFormatting.PierSide(State.MountPierSide);

    public bool ShowSlewUnsupportedNote => State.Mount.IsConnected && !State.MountCanSlew;

    public string SiteText =>
        AlignmentFormatting.Site(State.SiteLatitudeDegrees, State.SiteLongitudeDegrees, State.SiteHeightMeters);

    public bool IsSiteConfigured => State.IsSiteConfigured;

    public bool HasSiteDisagreement => State.SiteDisagreement is not null;

    public string? SiteDisagreement => State.SiteDisagreement;

    public string FocalLengthSummary =>
        AlignmentFormatting.FocalLength(State.FocalLengthMillimetres, State.IsFocalLengthSolved);

    public string PlateScaleText =>
        AlignmentFormatting.PlateScale(State.ScaleArcsecondsPerPixel, State.FieldRadiusDegrees);

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

    public string HemisphereText => State.SiteLatitudeDegrees is { } latitude
        ? $"{AlignmentFormatting.Hemisphere(latitude)} hemisphere site"
        : "Site not confirmed";

    public string ProgressText =>
        $"{State.CapturedPointCount} / {(State.PlannedPointCount > 0 ? State.PlannedPointCount.ToString(CultureInfo.InvariantCulture) : "?")} points captured";

    /// <summary>
    /// Non-null when the planner had to shrink the sweep. Uncertainty scales as
    /// the inverse square of the sweep, so this is a real degradation and not a
    /// detail: at half the requested sweep the answer is four times less
    /// certain.
    /// </summary>
    public string? SweepShortfall
    {
        get
        {
            if (State is not { PlannedSweepDegrees: { } planned, RequestedSweepDegrees: { } requested } ||
                planned >= requested - 0.5)
            {
                return null;
            }

            return FormattableString.Invariant($"The sweep had to shrink from {requested:F0}° to {planned:F0}°") +
                   " to keep every capture above the altitude floor on one side of the meridian. Uncertainty " +
                   "scales as the inverse square of the sweep, so expect roughly " +
                   FormattableString.Invariant($"{Math.Pow(requested / planned, 2.0):F1}×") +
                   " the uncertainty of a full sweep.";
        }
    }

    public bool HasSweepShortfall => SweepShortfall is not null;

    public bool HasProposal => State.Proposal is not null;

    public string? ProposalInstruction => State.Proposal?.Instruction;

    public bool ProposalRequiresMotion => State.Proposal?.RequiresMotion ?? false;

    public string ConfirmProposalText => ProposalRequiresMotion ? "Slew and capture" : "Capture here";

    public string ProposalDetail => State.Proposal is { } proposal
        ? FormattableString.Invariant($"Point {proposal.PointIndex} of {proposal.PlannedCaptures}") +
          FormattableString.Invariant($"  ·  hour angle {proposal.MechanicalRotationDegrees:F1}°") +
          FormattableString.Invariant($"  ·  predicted altitude {proposal.PredictedAltitudeDegrees:F0}°")
        : string.Empty;

    public bool HasWithheldReason => State.WithheldReason is not null;

    public string? WithheldReason => State.WithheldReason;

    public bool HasFaultReason => State.FaultReason is not null;

    public string? FaultReason => State.FaultReason;

    public bool HasCaptureWarning => State.CaptureWarning is not null;

    public string? CaptureWarning => State.CaptureWarning;

    public bool HasRejectionReason => State.RejectionReason is not null;

    public string? RejectionReason => State.RejectionReason;

    public bool HasManualInstruction => State.ManualInstruction is not null;

    public string? ManualInstruction => State.ManualInstruction;

    public IReadOnlyList<string> Log => State.Log;

    /// <summary>
    /// Polls the mount for where it is and whether it is tracking. Driven from
    /// the window's timer rather than from inside the engine, so the engine
    /// stays a deterministic function of the commands it is given.
    /// </summary>
    public Task RefreshMountStatusAsync() =>
        State.Mount.IsConnected ? SendAsync(new RefreshMountStatusCommand()) : Task.CompletedTask;

    // ---- Command bodies ----

    private async Task ConnectAsync(DeviceKind kind, DeviceOption? option)
    {
        if (option is null)
        {
            EntryError = $"Choose a {(kind == DeviceKind.Camera ? "camera" : "mount")} first.";
            return;
        }

        EntryError = null;
        await SendAsync(new ConnectDeviceCommand(kind, option.ProviderName, option.DeviceId)).ConfigureAwait(true);
    }

    private async Task ConfirmSiteAsync()
    {
        if (!TryParseDouble(LatitudeText, out double latitude) || Math.Abs(latitude) > 90.0)
        {
            EntryError = "Latitude must be a number of degrees between -90 and 90. North is positive.";
            return;
        }

        if (!TryParseDouble(LongitudeText, out double longitude) || Math.Abs(longitude) > 180.0)
        {
            EntryError = "Longitude must be a number of degrees between -180 and 180. East is positive.";
            return;
        }

        // Height is genuinely optional: it enters only through refraction and
        // some very small terms, so a blank field is treated as sea level rather
        // than blocking the one field that actually matters.
        if (!TryParseDouble(HeightText, out double height))
        {
            height = 0.0;
        }

        EntryError = null;
        await SendAsync(new ConfigureSiteCommand(latitude, longitude, height)).ConfigureAwait(true);
    }

    private async Task ApplyFocalLengthAsync()
    {
        if (string.IsNullOrWhiteSpace(FocalLengthText))
        {
            EntryError = null;
            await SendAsync(new ConfigureFocalLengthCommand(null)).ConfigureAwait(true);
            return;
        }

        if (!TryParseDouble(FocalLengthText, out double focalLength) || focalLength <= 0.0)
        {
            EntryError = "Focal length must be a positive number of millimetres.";
            return;
        }

        EntryError = null;
        await SendAsync(new ConfigureFocalLengthCommand(focalLength)).ConfigureAwait(true);
    }

    private async Task StartAsync()
    {
        if (!int.TryParse(CapturePointsText, NumberStyles.Integer, CultureInfo.InvariantCulture, out int points) || points < 3)
        {
            EntryError = "A sequence needs at least 3 capture points (D7).";
            return;
        }

        if (!TryParseDouble(SweepText, out double sweep) || sweep <= 0.0)
        {
            EntryError = "The sweep must be a positive number of degrees.";
            return;
        }

        EntryError = null;
        await SendAsync(new StartSessionCommand(new SessionConfiguration(
            points, sweep, DefaultConfiguration.ExposureDuration))).ConfigureAwait(true);
    }

    /// <summary>
    /// Sends whatever is in the coordinate fields. Nothing here decides whether
    /// that counts as an override -- the engine compares it against its own
    /// proposal, which is the only place that comparison can be made against the
    /// plan it belongs to.
    /// </summary>
    private async Task ConfirmProposalAsync()
    {
        if (State.Proposal is not { } proposal)
        {
            return;
        }

        if (!proposal.RequiresMotion)
        {
            EntryError = null;
            await SendAsync(new CaptureHereCommand()).ConfigureAwait(true);
            return;
        }

        if (!CoordinateText.TryParseRightAscension(ProposalRaText, out double ra))
        {
            EntryError =
                "Right ascension could not be read. Use hours and minutes (\"14:32:10\") or decimal degrees (\"218.04\").";
            return;
        }

        if (!CoordinateText.TryParseDeclination(ProposalDecText, out double dec))
        {
            EntryError =
                "Declination could not be read. Use degrees, arcminutes and arcseconds (\"-12 30 00\") or " +
                "decimal degrees (\"-12.5\").";
            return;
        }

        EntryError = null;
        await SendAsync(new ConfirmSlewCommand(ra, dec)).ConfigureAwait(true);
    }

    // ---- Event handling ----

    private void OnEngineEvent(EngineEvent engineEvent)
    {
        _log?.Record(engineEvent);

        UiState next = EngineEventReducer.Apply(_state, engineEvent);

        _postToUiThread(() =>
        {
            State = next;

            // Seeded, not bound: whatever the user has typed stays theirs until
            // a new proposal replaces it.
            if (engineEvent is SlewProposedEvent)
            {
                SeedProposalCoordinates(next.Proposal);
            }

            Remember(engineEvent);
        });
    }

    private void SeedProposalCoordinates(ProposalView? proposal)
    {
        if (proposal is null)
        {
            return;
        }

        // Decimal degrees rather than the sexagesimal form, so that confirming
        // without editing sends back exactly what was proposed and the engine
        // recognises it as unedited.
        ProposalRaText = CoordinateText.FormatDecimalDegrees(proposal.RaDegrees);
        ProposalDecText = CoordinateText.FormatDecimalDegrees(proposal.DecDegrees);
    }

    /// <summary>
    /// Persists the things worth carrying to the next session: the confirmed
    /// site, the focal length, and which devices were used. Written when they
    /// change rather than at shutdown, because the way an observing session
    /// usually ends is not a clean shutdown.
    /// </summary>
    private void Remember(EngineEvent engineEvent)
    {
        if (_settingsStore is null)
        {
            return;
        }

        AppSettings updated = engineEvent switch
        {
            SiteConfiguredEvent e => _settings with
            {
                Site = new StoredSite(e.LatitudeDegrees, e.LongitudeDegrees, e.HeightMeters),
            },

            EquipmentConfiguredEvent e => _settings with
            {
                FocalLengthMillimetres = e.FocalLengthMillimetres,
                IsFocalLengthSolved = e.IsFocalLengthSolved,
            },

            DeviceConnectedEvent { Kind: DeviceKind.Camera } e => _settings with
            {
                CameraProviderName = e.ProviderName,
                CameraDeviceId = e.DeviceId,
            },

            DeviceConnectedEvent { Kind: DeviceKind.Mount } e => _settings with
            {
                MountProviderName = e.ProviderName,
                MountDeviceId = e.DeviceId,
            },

            _ => _settings,
        };

        if (updated == _settings)
        {
            return;
        }

        _settings = updated;

        // A measured focal length is worth showing back in the field, since it
        // is now a better number than the one the user typed.
        if (engineEvent is EquipmentConfiguredEvent { IsFocalLengthSolved: true, FocalLengthMillimetres: { } solved })
        {
            FocalLengthText = solved.ToString("F1", CultureInfo.InvariantCulture);
        }

        string? problem = _settingsStore.Save(_settings);
        if (problem is not null)
        {
            _log?.Write(LogSeverity.Warning, problem);
            EntryError = problem;
        }
    }

    private static bool TryParseDouble(string? text, out double value) =>
        double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value) && double.IsFinite(value);

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
        foreach (string name in DerivedProperties)
        {
            OnPropertyChanged(name);
        }

        ConnectCameraCommand.RaiseCanExecuteChanged();
        DisconnectCameraCommand.RaiseCanExecuteChanged();
        ConnectMountCommand.RaiseCanExecuteChanged();
        DisconnectMountCommand.RaiseCanExecuteChanged();
        ConfirmSiteCommand.RaiseCanExecuteChanged();
        ApplyFocalLengthCommand.RaiseCanExecuteChanged();
        StartCommand.RaiseCanExecuteChanged();
        ConfirmProposalCommand.RaiseCanExecuteChanged();
        RestoreProposalCoordinatesCommand.RaiseCanExecuteChanged();
        CancelCommand.RaiseCanExecuteChanged();
        AbortCommand.RaiseCanExecuteChanged();
    }

    /// <summary>
    /// Every property computed from <see cref="State"/>. Listed once rather than
    /// as twenty-odd separate calls, so that adding a derived property and
    /// forgetting to notify it is a single visible omission instead of an
    /// invisible one.
    /// </summary>
    private static readonly string[] DerivedProperties =
    {
        nameof(IsSessionActive),
        nameof(IsAwaitingManualAction),
        nameof(StatusMessage),
        nameof(IsCameraConnected),
        nameof(IsMountConnected),
        nameof(CameraSummary),
        nameof(CameraPixelText),
        nameof(MountSummary),
        nameof(MountCoordinatesText),
        nameof(TrackingText),
        nameof(IsTracking),
        nameof(PierSideText),
        nameof(ShowSlewUnsupportedNote),
        nameof(SiteText),
        nameof(IsSiteConfigured),
        nameof(HasSiteDisagreement),
        nameof(SiteDisagreement),
        nameof(FocalLengthSummary),
        nameof(PlateScaleText),
        nameof(HasCurrentEstimate),
        nameof(IsGoodEnoughToStop),
        nameof(AltitudeErrorText),
        nameof(AzimuthErrorText),
        nameof(TotalErrorText),
        nameof(ResidualRmsText),
        nameof(HemisphereText),
        nameof(ProgressText),
        nameof(SweepShortfall),
        nameof(HasSweepShortfall),
        nameof(HasProposal),
        nameof(ProposalInstruction),
        nameof(ProposalRequiresMotion),
        nameof(ConfirmProposalText),
        nameof(ProposalDetail),
        nameof(HasWithheldReason),
        nameof(WithheldReason),
        nameof(HasFaultReason),
        nameof(FaultReason),
        nameof(HasCaptureWarning),
        nameof(CaptureWarning),
        nameof(HasRejectionReason),
        nameof(RejectionReason),
        nameof(HasManualInstruction),
        nameof(ManualInstruction),
        nameof(Log),
    };

    public void Dispose() => _subscription?.Dispose();
}
