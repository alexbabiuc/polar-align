using FreePolarAlign.Core.Alignment;
using FreePolarAlign.Core.Astrometry;
using FreePolarAlign.Core.Engine;
using FreePolarAlign.Devices;
using FreePolarAlign.Imaging.Wcs;
using FreePolarAlign.Solving;

namespace FreePolarAlign.Session;

/// <summary>
/// Emitted when the operator has to do something before the sequence can go on.
/// Added for D10, which makes manual mounts a first-class mode rather than a
/// fallback: the algorithm does not care how the mount reached each position, so
/// a prompt and a slew are interchangeable from the engine's point of view.
/// </summary>
public sealed record ManualActionRequiredEvent(string Instruction, double RotationDegrees) : EngineEvent;

/// <summary>
/// A single capture failed. Distinct from a session fault because the usual
/// cause -- a cloud, a satellite, a momentarily poor solve -- is worth retrying
/// rather than abandoning the sequence over.
/// </summary>
/// <param name="WillRetry">
/// Whether the engine is still prepared to try this capture again. False means
/// a session fault follows immediately, so a caller need not guess.
/// </param>
public sealed record CaptureFailedEvent(int CaptureIndex, string Reason, bool WillRetry) : EngineEvent;

/// <summary>
/// A command arrived that the engine will not act on in its current state --
/// starting a session with no camera connected, confirming a slew when nothing
/// has been proposed, disconnecting mid-sequence.
///
/// Deliberately not a <see cref="SessionFaultedEvent"/>: nothing has broken and
/// no state has been torn down, so presenting it as a fault would train the user
/// to ignore faults. Deliberately not silence either -- a button that appears to
/// do nothing is worse than one that explains itself.
/// </summary>
public sealed record CommandRejectedEvent(string Reason) : EngineEvent;

/// <param name="ManualMode">
/// D10: prompt the operator to turn the mount by hand instead of slewing. Costs
/// almost nothing given the geometry, and covers mounts with no driver or a
/// misbehaving one.
/// </param>
/// <param name="ExpectedSolveNoiseArcseconds">
/// Per-observation plate-solve accuracy, which sets the scale of the reported
/// covariance and the yardstick residuals are judged against. Phase 2 measured
/// well under an arcsecond on synthetic frames; the default here is deliberately
/// more pessimistic, since a real sky adds differential refraction, optical
/// distortion and seeing-driven centroid wander.
/// </param>
public sealed record AlignmentSessionOptions(
    int CaptureCount = 6,
    double SweepDegrees = 70.0,
    TimeSpan ExposureDuration = default,
    bool ManualMode = false,
    double ExpectedSolveNoiseArcseconds = 3.0,
    AtmosphericConditions? Atmosphere = null,
    EquipmentProfile? EquipmentProfile = null)
{
    public TimeSpan EffectiveExposure => ExposureDuration == default ? TimeSpan.FromSeconds(2) : ExposureDuration;

    /// <summary>
    /// Conditions to use, defaulting to a standard atmosphere rather than a
    /// vacuum. Refraction is tens of arcseconds and varies with altitude across
    /// a sequence, so assuming it away puts a systematic, altitude-dependent
    /// error into every observation (D15). Supply measured weather when it is
    /// available.
    /// </summary>
    public AtmosphericConditions EffectiveAtmosphere => Atmosphere ?? AtmosphericConditions.Standard;
}

/// <summary>
/// The alignment sequence: connect, confirm where you are, capture, solve, fit,
/// and report -- with a way out at every step, and with the mount standing still
/// until told otherwise.
///
/// The engine is driven by commands and answers only with events (D6), so the
/// same object serves a local UI now and a remote one later without change. It
/// deliberately exposes no "current state" property: callers reconstruct
/// whatever they need from the event stream, which is what keeps the boundary
/// message-shaped rather than merely message-flavoured.
///
/// Nothing here ever commands motion on its own initiative (D18). Every slew is
/// the direct consequence of a <see cref="CaptureNextPointCommand"/> or
/// <see cref="ConfirmSlewCommand"/> that arrived from outside, and after each
/// capture the engine proposes and then waits.
/// </summary>
public sealed class AlignmentSession : IAlignmentEngine, IDisposable
{
    private readonly ISolver _solver;
    private readonly AlignmentSessionOptions _options;
    private readonly DeviceCatalog? _catalog;
    private readonly EventStream _events = new();
    private readonly SemaphoreSlim _commandGate = new(1, 1);

    /// <summary>
    /// How many failed solves in a row before the session gives up. Retrying is
    /// right for transient trouble; past a handful the cause is structural --
    /// the wrong index pack, a badly wrong focal length, thick cloud -- and
    /// silently retrying forever would leave the user watching nothing happen.
    /// </summary>
    private const int MaximumConsecutiveSolveFailures = 3;

    /// <summary>
    /// How far the mount's own site may differ from the confirmed one before it
    /// is worth mentioning. A latitude error appears one-for-one in the reported
    /// altitude misalignment (D19), so a hundredth of a degree is already six
    /// tenths of an arcminute of pure bias -- large against the arcminute the UI
    /// reports to.
    /// </summary>
    private const double SiteLatitudeDisagreementDegrees = 0.01;

    private const double SiteLongitudeDisagreementDegrees = 0.05;

    private const double SiteHeightDisagreementMeters = 200.0;

    /// <summary>
    /// How far a hand-typed declination may sit from the sequence's mechanical
    /// declination and still be read as "the same target, a different hour
    /// angle" rather than "a different part of the sky".
    ///
    /// Not an accuracy tolerance -- five arcminutes of real declination movement
    /// would wreck the fit, and D11 would catch it. It is a question about
    /// intent, and once answered the declination is snapped to the sequence's
    /// exactly rather than left five arcminutes out.
    /// </summary>
    private const double SameTargetDeclinationToleranceArcminutes = 5.0;

    private readonly List<HorizontalCoordinates> _observations = new();

    private ICamera? _camera;
    private IMount? _mount;
    private bool _ownsCamera;
    private bool _ownsMount;

    /// <summary>
    /// Capture count and sweep for the sequence now running, taken from the
    /// start command and falling back to the constructor options. Held as
    /// session state rather than read from either source at each use, so that a
    /// re-anchor mid-sequence re-plans with the same parameters the sequence
    /// began with.
    /// </summary>
    private int _captureCount;

    private double _sweepDegrees;

    private TimeSpan _exposure;

    private GeodeticLocation? _site;
    private TargetPlan? _plan;
    private PendingProposal? _proposal;
    private PierSide _initialPierSide = PierSide.Unknown;
    private double _initialRotationSign;
    private int _completedCaptures;
    private int _consecutiveSolveFailures;
    private bool _sessionActive;
    private EquipmentProfile? _profile;

    /// <summary>
    /// What the engine has suggested and is waiting on. Held as one object so
    /// that "is a slew pending?" is a single null check rather than a set of
    /// flags that can disagree with each other.
    /// </summary>
    private sealed record PendingProposal(
        int PointIndex,
        double MechanicalRotationDegrees,
        double RaDegrees,
        double DecDegrees,
        bool RequiresMotion);

    /// <summary>
    /// Constructs a session that opens its own devices on command. This is the
    /// form the application uses: device selection is the user's, made after
    /// startup, from whatever the catalogue found.
    /// </summary>
    public AlignmentSession(DeviceCatalog catalog, ISolver solver, AlignmentSessionOptions? options = null)
    {
        _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
        _solver = solver ?? throw new ArgumentNullException(nameof(solver));
        _options = options ?? new AlignmentSessionOptions();
        _profile = _options.EquipmentProfile;
    }

    /// <summary>
    /// Constructs a session over devices the caller already holds. The devices
    /// count as selected but not yet connected, so the ordinary
    /// connect-then-start sequence still applies -- and the caller keeps
    /// ownership, so the session will not dispose them.
    /// </summary>
    public AlignmentSession(ICamera camera, IMount mount, ISolver solver, AlignmentSessionOptions? options = null)
    {
        _camera = camera ?? throw new ArgumentNullException(nameof(camera));
        _mount = mount ?? throw new ArgumentNullException(nameof(mount));
        _solver = solver ?? throw new ArgumentNullException(nameof(solver));
        _options = options ?? new AlignmentSessionOptions();
        _profile = _options.EquipmentProfile;

    }

    /// <summary>
    /// The provider name reported for devices handed to the convenience
    /// constructor. They were not discovered through a catalogue, so they need a
    /// name of their own to appear under.
    /// </summary>
    public const string AttachedProviderName = "Attached";

    public IObservable<EngineEvent> Events => _events;

    /// <summary>
    /// The equipment profile as it now stands, including a focal length learned
    /// from a solve. Worth persisting between sessions: the first solve is the
    /// expensive one, and after it the scale is known well enough to hint.
    /// </summary>
    public EquipmentProfile? Profile => _profile;

    public async ValueTask SendAsync(EngineCommand command, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);

        await _commandGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            switch (command)
            {
                case ConnectDeviceCommand connect:
                    await ConnectAsync(connect, cancellationToken).ConfigureAwait(false);
                    break;
                case DisconnectDeviceCommand disconnect:
                    await DisconnectAsync(disconnect.Kind, cancellationToken).ConfigureAwait(false);
                    break;
                case ConfigureSiteCommand site:
                    await ConfigureSiteAsync(site, cancellationToken).ConfigureAwait(false);
                    break;
                case ConfigureFocalLengthCommand focalLength:
                    ConfigureFocalLength(focalLength);
                    break;
                case RefreshMountStatusCommand:
                    await RefreshMountStatusAsync(cancellationToken).ConfigureAwait(false);
                    break;
                case StartSessionCommand start:
                    await StartAsync(start, cancellationToken).ConfigureAwait(false);
                    break;
                case CaptureHereCommand:
                    await CaptureHereAsync(cancellationToken).ConfigureAwait(false);
                    break;
                case CaptureNextPointCommand:
                    await CaptureProposedAsync(cancellationToken).ConfigureAwait(false);
                    break;
                case ConfirmSlewCommand confirm:
                    await ConfirmSlewAsync(confirm, cancellationToken).ConfigureAwait(false);
                    break;
                case CancelSessionCommand:
                    ResetSession();
                    _events.Publish(new SessionCompletedEvent());
                    break;
                case AbortSessionCommand abort:
                    ResetSession();
                    _events.Publish(new SessionFaultedEvent(abort.Reason));
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(command), command, "Unrecognised engine command.");
            }
        }
        finally
        {
            _commandGate.Release();
        }
    }

    // ---- Devices ----

    private async Task ConnectAsync(ConnectDeviceCommand command, CancellationToken cancellationToken)
    {
        if (_sessionActive)
        {
            _events.Publish(new CommandRejectedEvent(
                "Cannot change devices while a sequence is running. Cancel it first."));
            return;
        }

        try
        {
            if (command.Kind == DeviceKind.Camera)
            {
                await ReplaceCameraAsync(command, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                await ReplaceMountAsync(command, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            // A driver that will not open is the single most common thing to go
            // wrong at the start of a night, and the message the driver gives is
            // usually the most useful thing anyone will see about it.
            _events.Publish(new DeviceDisconnectedEvent(
                command.Kind,
                $"Could not connect {Describe(command.Kind)} '{command.DeviceId}' from provider " +
                $"'{command.ProviderName}': {ex.Message}"));
        }
    }

    private async Task ReplaceCameraAsync(ConnectDeviceCommand command, CancellationToken cancellationToken)
    {
        ICamera camera = ResolveCamera(command);

        if (!ReferenceEquals(camera, _camera))
        {
            await ReleaseCameraAsync(cancellationToken).ConfigureAwait(false);
            _camera = camera;
            _ownsCamera = _catalog is not null;
        }

        if (!camera.IsConnected)
        {
            await camera.ConnectAsync(cancellationToken).ConfigureAwait(false);
        }

        // The profile is rebuilt from the camera that is actually attached, so a
        // pixel pitch from a previous camera cannot survive a swap and quietly
        // corrupt every scale hint that follows. A focal length already measured
        // is kept, because it belongs to the telescope rather than the camera --
        // but it stops counting as measured, since the scale it was measured
        // from was this camera's predecessor's.
        double? focalLength = _profile?.FocalLengthMillimetres;
        _profile = new EquipmentProfile(
            camera.Name, camera.PixelSizeMicrons, camera.SensorWidthPixels, camera.SensorHeightPixels,
            focalLength, IsFocalLengthSolved: false);

        _events.Publish(new DeviceConnectedEvent(
            DeviceKind.Camera,
            command.ProviderName,
            command.DeviceId,
            camera.Name,
            DescribeDriver(command),
            new CameraDescription(camera.PixelSizeMicrons, camera.SensorWidthPixels, camera.SensorHeightPixels)));

        PublishEquipment();
    }

    private async Task ReplaceMountAsync(ConnectDeviceCommand command, CancellationToken cancellationToken)
    {
        IMount mount = ResolveMount(command);

        if (!ReferenceEquals(mount, _mount))
        {
            await ReleaseMountAsync(cancellationToken).ConfigureAwait(false);
            _mount = mount;
            _ownsMount = _catalog is not null;
        }

        if (!mount.IsConnected)
        {
            await mount.ConnectAsync(cancellationToken).ConfigureAwait(false);
        }

        _events.Publish(new DeviceConnectedEvent(
            DeviceKind.Mount,
            command.ProviderName,
            command.DeviceId,
            mount.Name,
            DescribeDriver(command),
            Camera: null,
            CanSlew: mount.CanSlewAsync));

        await RefreshMountStatusAsync(cancellationToken).ConfigureAwait(false);
    }

    private ICamera ResolveCamera(ConnectDeviceCommand command)
    {
        if (_catalog is not null)
        {
            return _catalog.OpenCamera(command.ProviderName, command.DeviceId);
        }

        return _camera ?? throw new InvalidOperationException(
            "This session was constructed without a device catalogue, so it can only connect the camera it was given.");
    }

    private IMount ResolveMount(ConnectDeviceCommand command)
    {
        if (_catalog is not null)
        {
            return _catalog.OpenMount(command.ProviderName, command.DeviceId);
        }

        return _mount ?? throw new InvalidOperationException(
            "This session was constructed without a device catalogue, so it can only connect the mount it was given.");
    }

    private async Task DisconnectAsync(DeviceKind kind, CancellationToken cancellationToken)
    {
        if (_sessionActive)
        {
            _events.Publish(new CommandRejectedEvent(
                $"Cannot disconnect the {Describe(kind)} while a sequence is running. Cancel it first."));
            return;
        }

        if (kind == DeviceKind.Camera)
        {
            await ReleaseCameraAsync(cancellationToken).ConfigureAwait(false);
        }
        else
        {
            await ReleaseMountAsync(cancellationToken).ConfigureAwait(false);
        }

        _events.Publish(new DeviceDisconnectedEvent(kind));
    }

    private async Task ReleaseCameraAsync(CancellationToken cancellationToken)
    {
        if (_camera is null)
        {
            return;
        }

        ICamera camera = _camera;
        bool owned = _ownsCamera;
        _camera = null;
        _ownsCamera = false;

        try
        {
            if (camera.IsConnected)
            {
                await camera.DisconnectAsync(cancellationToken).ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // A driver that throws on the way out has still been let go of.
        }

        if (owned)
        {
            camera.Dispose();
        }
    }

    private async Task ReleaseMountAsync(CancellationToken cancellationToken)
    {
        if (_mount is null)
        {
            return;
        }

        IMount mount = _mount;
        bool owned = _ownsMount;
        _mount = null;
        _ownsMount = false;

        try
        {
            if (mount.IsConnected)
            {
                await mount.DisconnectAsync(cancellationToken).ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
        }

        if (owned)
        {
            mount.Dispose();
        }
    }

    private async Task RefreshMountStatusAsync(CancellationToken cancellationToken)
    {
        if (_mount is null || !_mount.IsConnected)
        {
            return;
        }

        try
        {
            MountPosition position = await _mount.GetPositionAsync(cancellationToken).ConfigureAwait(false);
            _events.Publish(new MountStatusEvent(
                position.RaDegrees,
                position.DecDegrees,
                MapTracking(position.Tracking),
                MapPierSide(position.PierSide)));
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            // Status polling runs continuously, so a mount that has dropped off
            // the bus must report that once rather than fault the application
            // several times a second.
            if (_sessionActive)
            {
                ResetSession();
                _events.Publish(new SessionFaultedEvent($"Lost contact with the mount: {ex.Message}"));
            }
            else
            {
                _events.Publish(new DeviceDisconnectedEvent(DeviceKind.Mount, $"Lost contact with the mount: {ex.Message}"));
                await ReleaseMountAsync(CancellationToken.None).ConfigureAwait(false);
            }
        }
    }

    // ---- Site and equipment ----

    private async Task ConfigureSiteAsync(ConfigureSiteCommand command, CancellationToken cancellationToken)
    {
        if (Math.Abs(command.LatitudeDegrees) > 90.0 || Math.Abs(command.LongitudeDegrees) > 180.0)
        {
            _events.Publish(new CommandRejectedEvent(
                "That site is not on Earth: latitude must be within ±90° and longitude within ±180°."));
            return;
        }

        if (_sessionActive)
        {
            _events.Publish(new CommandRejectedEvent(
                "Cannot change the site while a sequence is running -- every capture already taken was reduced " +
                "with the old one. Cancel the sequence and start again."));
            return;
        }

        var site = new GeodeticLocation(command.LatitudeDegrees, command.LongitudeDegrees, command.HeightMeters);
        _site = site;

        string? disagreement = await DescribeSiteDisagreementAsync(site, cancellationToken).ConfigureAwait(false);

        _events.Publish(new SiteConfiguredEvent(
            site.LatitudeDegrees, site.LongitudeDegrees, site.HeightMeters, disagreement));
    }

    /// <summary>
    /// Compares the confirmed site against the mount's own, and describes the
    /// difference if there is one worth describing.
    ///
    /// The confirmed figure wins regardless (D19). A driver's site is very often
    /// a factory default or a leftover from wherever the mount was last set up,
    /// and the user has just been asked to look at theirs -- but a disagreement
    /// is also the clearest early sign that one of the two is wrong, so it is
    /// reported rather than resolved.
    /// </summary>
    private async Task<string?> DescribeSiteDisagreementAsync(GeodeticLocation confirmed, CancellationToken cancellationToken)
    {
        if (_mount is null || !_mount.IsConnected)
        {
            return null;
        }

        GeodeticLocation reported;
        try
        {
            reported = await _mount.GetSiteLocationAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return null;
        }

        double latitudeDifference = Math.Abs(reported.LatitudeDegrees - confirmed.LatitudeDegrees);
        double longitudeDifference = Math.Abs(reported.LongitudeDegrees - confirmed.LongitudeDegrees);
        double heightDifference = Math.Abs(reported.HeightMeters - confirmed.HeightMeters);

        if (latitudeDifference <= SiteLatitudeDisagreementDegrees &&
            longitudeDifference <= SiteLongitudeDisagreementDegrees &&
            heightDifference <= SiteHeightDisagreementMeters)
        {
            return null;
        }

        string message =
            $"The mount reports its site as {reported.LatitudeDegrees:F4}°, {reported.LongitudeDegrees:F4}°, " +
            $"{reported.HeightMeters:F0} m, which differs from the site you confirmed. Yours is being used.";

        if (latitudeDifference > SiteLatitudeDisagreementDegrees)
        {
            // Worth spelling out, because this is the one difference that turns
            // straight into a wrong answer rather than a slightly wrong
            // prediction.
            message +=
                $" The latitudes differ by {latitudeDifference * 60.0:F1}', and a latitude error appears " +
                "one-for-one in the altitude figure this software reports, so it is worth settling which is right.";
        }

        return message;
    }

    private void ConfigureFocalLength(ConfigureFocalLengthCommand command)
    {
        if (command.FocalLengthMillimetres is { } focalLength &&
            (!double.IsFinite(focalLength) || focalLength <= 0.0))
        {
            _events.Publish(new CommandRejectedEvent("A focal length has to be a positive number of millimetres."));
            return;
        }

        if (_profile is null)
        {
            _events.Publish(new CommandRejectedEvent(
                "Connect a camera first -- the plate scale needs its pixel size as well as the focal length."));
            return;
        }

        // A figure typed by a person replaces a measured one only because they
        // asked it to, and it stops being marked as measured: the whole value of
        // the distinction is that it decides how far the solver is allowed to
        // trust the hint.
        _profile = _profile with
        {
            FocalLengthMillimetres = command.FocalLengthMillimetres,
            IsFocalLengthSolved = false,
        };

        PublishEquipment();
    }

    private void PublishEquipment() => _events.Publish(new EquipmentConfiguredEvent(
        _profile?.FocalLengthMillimetres,
        _profile?.IsFocalLengthSolved ?? false,
        _profile?.ExpectedScaleArcsecondsPerPixel,
        _profile?.ExpectedFieldRadiusDegrees));

    // ---- Starting a sequence ----

    private async Task StartAsync(StartSessionCommand start, CancellationToken cancellationToken)
    {
        ResetSession();

        if (_camera is null || !_camera.IsConnected)
        {
            _events.Publish(new CommandRejectedEvent("Connect a camera before starting a sequence."));
            return;
        }

        if (_mount is null || !_mount.IsConnected)
        {
            _events.Publish(new CommandRejectedEvent("Connect a mount before starting a sequence."));
            return;
        }

        if (_site is null)
        {
            _events.Publish(new CommandRejectedEvent(
                "Confirm the observing site before starting a sequence. The latitude in particular goes straight " +
                "into the answer (D19), so it is not something to be inherited silently."));
            return;
        }

        if (!_options.ManualMode && !_mount.CanSlewAsync)
        {
            _events.Publish(new CommandRejectedEvent(
                "This mount cannot perform absolute slews (D9), so automatic mode is unavailable. Use manual mode."));
            return;
        }

        // The command's parameters win where it supplies them, so a UI can offer
        // them without the engine having to be reconstructed; the constructor
        // options remain the source for everything the UI does not expose.
        _captureCount = start.Configuration.CapturePoints > 0
            ? start.Configuration.CapturePoints
            : _options.CaptureCount;
        _sweepDegrees = start.Configuration.RequestedSweepDegrees > 0.0
            ? start.Configuration.RequestedSweepDegrees
            : _options.SweepDegrees;
        _exposure = start.Configuration.ExposureDuration > TimeSpan.Zero
            ? start.Configuration.ExposureDuration
            : _options.EffectiveExposure;

        if (_captureCount < SmallCircleFitter.MinimumObservations)
        {
            _events.Publish(new CommandRejectedEvent(
                $"A sequence needs at least {SmallCircleFitter.MinimumObservations} captures (D7); " +
                $"{_captureCount} was requested."));
            return;
        }

        try
        {
            // Where the mount believes it is. Its belief, not a solved position,
            // is the right anchor: the mechanical declination the sequence must
            // hold constant is defined in the frame the mount drives in.
            MountPosition position = await _mount.GetPositionAsync(cancellationToken).ConfigureAwait(false);
            double rotation = TargetSelection.MechanicalRotationOf(
                _site, position.RaDegrees, position.DecDegrees, DateTime.UtcNow);
            double declination = MechanicalDeclinationOf(_site, position.RaDegrees, position.DecDegrees, DateTime.UtcNow);

            TargetPlan plan = TargetSelection.PlanFrom(
                _site, rotation, declination, DateTime.UtcNow,
                _captureCount, _sweepDegrees, _options.EffectiveAtmosphere);

            if (!plan.Success)
            {
                _events.Publish(new SessionFaultedEvent(plan.Reason ?? "No usable target could be selected."));
                return;
            }

            _plan = plan;
            _sessionActive = true;

            _events.Publish(new SessionStartedEvent(start.Configuration));
            _events.Publish(new TargetSelectedEvent(
                plan.MechanicalDeclinationDegrees, plan.IsWest, plan.Captures.Count, plan.SweepDegrees));

            ProposeNext();
        }
        catch (OperationCanceledException)
        {
            ResetSession();
            throw;
        }
        catch (Exception ex)
        {
            ResetSession();
            _events.Publish(new SessionFaultedEvent($"Could not start the session: {ex.Message}"));
        }
    }

    /// <summary>
    /// Works out and announces the next point, then stops. This is the whole of
    /// D18: the engine's response to a completed capture is a suggestion, never a
    /// movement.
    /// </summary>
    private void ProposeNext()
    {
        if (_plan is null || _site is null)
        {
            return;
        }

        if (_completedCaptures >= _plan.Captures.Count)
        {
            _sessionActive = false;
            _proposal = null;
            _events.Publish(new SessionCompletedEvent());
            return;
        }

        PlannedCapture planned = _plan.Captures[_completedCaptures];
        int pointIndex = _completedCaptures + 1;

        // The first capture of a plan anchored at the current pointing needs no
        // motion at all, and neither does any capture on a mount being turned by
        // hand.
        bool requiresMotion = !(_completedCaptures == 0 && _plan.AnchoredAtCurrentPointing) && !_options.ManualMode;

        (double ra, double dec) = TargetSelection.ResolveCommand(
            _site, planned.MechanicalRotationDegrees, _plan.MechanicalDeclinationDegrees, DateTime.UtcNow);

        _proposal = new PendingProposal(pointIndex, planned.MechanicalRotationDegrees, ra, dec, requiresMotion);

        _events.Publish(new SlewProposedEvent(
            pointIndex,
            _plan.Captures.Count,
            ra,
            dec,
            planned.MechanicalRotationDegrees,
            planned.PredictedAltitudeDegrees,
            requiresMotion,
            BuildInstruction(planned, pointIndex, _plan.Captures.Count, requiresMotion)));

        if (_options.ManualMode)
        {
            // D10's own event as well, so a UI built around manual mounts can
            // present the instruction without having to interpret a proposal
            // that it knows will never involve a slew.
            _events.Publish(new ManualActionRequiredEvent(
                BuildInstruction(planned, pointIndex, _plan.Captures.Count, requiresMotion),
                planned.MechanicalRotationDegrees));
        }
    }

    private string BuildInstruction(PlannedCapture planned, int pointIndex, int total, bool requiresMotion)
    {
        string direction = planned.MechanicalRotationDegrees >= 0 ? "west" : "east";
        string where = $"{Math.Abs(planned.MechanicalRotationDegrees):F0}° {direction} of the meridian, " +
                       $"predicted altitude {planned.PredictedAltitudeDegrees:F0}°";

        if (_options.ManualMode)
        {
            return $"Point {pointIndex} of {total}: rotate the mount in RA by hand to about {where}. " +
                   "Do not touch declination -- the sequence depends on it staying where it is (D11). " +
                   "Then capture.";
        }

        if (!requiresMotion)
        {
            return $"Point {pointIndex} of {total}: the telescope is already somewhere usable " +
                   $"({where}). Capture here first -- nothing needs to move, and this frame is what " +
                   "measures the focal length for every solve after it.";
        }

        return $"Point {pointIndex} of {total}: slew to {where}. Check the way is clear, then confirm. " +
               "Edit the coordinates if that part of the sky is blocked.";
    }

    // ---- Capturing ----

    private async Task CaptureHereAsync(CancellationToken cancellationToken)
    {
        if (!EnsureCapturable(out PendingProposal? proposal))
        {
            return;
        }

        if (proposal!.RequiresMotion && !_options.ManualMode)
        {
            // Capturing without moving when the plan calls for a slew is not
            // refused -- the user can see the sky and may have moved the mount
            // themselves -- but the point will land off the planned arc, so the
            // sequence is re-anchored on it rather than pretending it was the
            // planned position.
            await ReanchorHereAsync(
                "Captured without slewing, so the sequence was re-anchored on the current position.",
                cancellationToken).ConfigureAwait(false);
            return;
        }

        await CaptureAndSolveAsync(commandedMotion: false, 0.0, 0.0, cancellationToken).ConfigureAwait(false);
    }

    private async Task CaptureProposedAsync(CancellationToken cancellationToken)
    {
        if (!EnsureCapturable(out PendingProposal? proposal))
        {
            return;
        }

        if (!proposal!.RequiresMotion)
        {
            await CaptureAndSolveAsync(commandedMotion: false, 0.0, 0.0, cancellationToken).ConfigureAwait(false);
            return;
        }

        // Re-resolved for now rather than reusing the coordinates announced with
        // the proposal. A fixed sky coordinate does not hold the mechanical
        // declination constant while the sky turns, and commanding a stale one
        // moves the mount in declination by minutes of arc over a wide sweep
        // (D16) -- which is exactly what invalidates the fit.
        (double ra, double dec) = TargetSelection.ResolveCommand(
            _site!, proposal.MechanicalRotationDegrees, _plan!.MechanicalDeclinationDegrees, DateTime.UtcNow);

        _events.Publish(new SlewConfirmedEvent(ra, dec, WasOverridden: false));
        await CaptureAndSolveAsync(commandedMotion: true, ra, dec, cancellationToken).ConfigureAwait(false);
    }

    private async Task ConfirmSlewAsync(ConfirmSlewCommand command, CancellationToken cancellationToken)
    {
        if (!EnsureCapturable(out PendingProposal? proposal))
        {
            return;
        }

        if (!double.IsFinite(command.RaDegrees) || !double.IsFinite(command.DecDegrees) ||
            command.RaDegrees is < 0.0 or >= 360.0 || Math.Abs(command.DecDegrees) > 90.0)
        {
            _events.Publish(new CommandRejectedEvent(
                "Those coordinates are not on the sky: right ascension must be within [0°, 360°) and " +
                "declination within ±90°."));
            return;
        }

        // Accepting the proposal unedited goes down the ordinary path, so that a
        // user who simply confirms gets coordinates resolved for the moment of
        // the slew rather than the moment of the suggestion.
        if (IsEffectivelyUnchanged(command, proposal!))
        {
            await CaptureProposedAsync(cancellationToken).ConfigureAwait(false);
            return;
        }

        double rotation = TargetSelection.MechanicalRotationOf(
            _site!, command.RaDegrees, command.DecDegrees, DateTime.UtcNow);
        double declination = MechanicalDeclinationOf(
            _site!, command.RaDegrees, command.DecDegrees, DateTime.UtcNow);

        double declinationDifferenceArcminutes =
            Math.Abs(declination - _plan!.MechanicalDeclinationDegrees) * 60.0;

        if (declinationDifferenceArcminutes > SameTargetDeclinationToleranceArcminutes)
        {
            await ReanchorAtAsync(
                command.RaDegrees, command.DecDegrees, rotation, declination,
                $"Those coordinates sit {declinationDifferenceArcminutes:F0}' away in declination, which is a " +
                "different target rather than a different hour angle on the same one. Points at different " +
                "declinations do not lie on one circle about the polar axis, so the earlier captures have been " +
                "discarded and the sequence restarted here.",
                cancellationToken).ConfigureAwait(false);
            return;
        }

        // Same target, different hour angle. The declination is snapped back to
        // the sequence's exactly: leaving it a few arcminutes out would put a
        // real declination movement into the data, which is precisely what the
        // sequence must not contain.
        (double ra, double dec) = TargetSelection.ResolveCommand(
            _site!, rotation, _plan.MechanicalDeclinationDegrees, DateTime.UtcNow);

        _proposal = proposal! with { MechanicalRotationDegrees = rotation, RaDegrees = ra, DecDegrees = dec };
        _plan = _plan with { Captures = ReplaceCapture(_plan.Captures, _completedCaptures, rotation) };

        _events.Publish(new SlewConfirmedEvent(ra, dec, WasOverridden: true));
        await CaptureAndSolveAsync(commandedMotion: true, ra, dec, cancellationToken).ConfigureAwait(false);
    }

    private async Task ReanchorHereAsync(string reason, CancellationToken cancellationToken)
    {
        MountPosition position = await _mount!.GetPositionAsync(cancellationToken).ConfigureAwait(false);
        double rotation = TargetSelection.MechanicalRotationOf(
            _site!, position.RaDegrees, position.DecDegrees, DateTime.UtcNow);
        double declination = MechanicalDeclinationOf(
            _site!, position.RaDegrees, position.DecDegrees, DateTime.UtcNow);

        if (!Reanchor(rotation, declination, reason, moved: false))
        {
            return;
        }

        await CaptureAndSolveAsync(commandedMotion: false, 0.0, 0.0, cancellationToken).ConfigureAwait(false);
    }

    private async Task ReanchorAtAsync(
        double raDegrees,
        double decDegrees,
        double rotation,
        double declination,
        string reason,
        CancellationToken cancellationToken)
    {
        if (!Reanchor(rotation, declination, reason, moved: true))
        {
            return;
        }

        _events.Publish(new SlewConfirmedEvent(raDegrees, decDegrees, WasOverridden: true, ReanchoredReason: reason));
        await CaptureAndSolveAsync(commandedMotion: true, raDegrees, decDegrees, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Throws away the captures taken so far and re-plans from a new position.
    ///
    /// Discarding is the point. A small-circle fit assumes every observation lies
    /// on one circle about the polar axis; mixing declinations breaks that
    /// assumption, and the fit would not simply be noisier, it would be
    /// confidently wrong -- the failure D11 exists to prevent. Keeping the old
    /// points would be the more generous-looking choice and the more dangerous
    /// one.
    /// </summary>
    private bool Reanchor(double rotation, double declination, string reason, bool moved)
    {
        TargetPlan plan = TargetSelection.PlanFrom(
            _site!, rotation, declination, DateTime.UtcNow,
            _captureCount, _sweepDegrees, _options.EffectiveAtmosphere);

        if (!plan.Success)
        {
            ResetSession();
            _events.Publish(new SessionFaultedEvent(
                $"{reason} No usable sequence can be planned from there: {plan.Reason}"));
            return false;
        }

        _observations.Clear();
        _completedCaptures = 0;
        _consecutiveSolveFailures = 0;
        _initialPierSide = PierSide.Unknown;
        _initialRotationSign = 0.0;
        _plan = plan;

        _events.Publish(new AlignmentWithheldEvent(reason));
        _events.Publish(new TargetSelectedEvent(
            plan.MechanicalDeclinationDegrees, plan.IsWest, plan.Captures.Count, plan.SweepDegrees));

        _proposal = new PendingProposal(1, rotation, 0.0, 0.0, RequiresMotion: moved);
        return true;
    }

    private async Task CaptureAndSolveAsync(
        bool commandedMotion,
        double commandRa,
        double commandDec,
        CancellationToken cancellationToken)
    {
        try
        {
            if (commandedMotion)
            {
                await _mount!.SlewToCoordinatesAsync(commandRa, commandDec, cancellationToken).ConfigureAwait(false);
            }

            // Where the mount ended up, which is what the safety checks and the
            // solver hint must both be based on -- not where it was asked to go,
            // since a capture taken without motion was never asked anything.
            MountPosition position = await _mount!.GetPositionAsync(cancellationToken).ConfigureAwait(false);

            // Reported onwards because it has already been paid for. A UI
            // polling on its own timer would otherwise show the pre-slew
            // position for another second or two after every move, which reads
            // as a mount that has not gone anywhere.
            _events.Publish(new MountStatusEvent(
                position.RaDegrees,
                position.DecDegrees,
                MapTracking(position.Tracking),
                MapPierSide(position.PierSide)));

            if (!await PassesSafetyChecksAsync(position, cancellationToken).ConfigureAwait(false))
            {
                return;
            }

            CapturedImage captured = await _camera!
                .ExposeAsync(_exposure, cancellationToken).ConfigureAwait(false);

            PlateSolveResult solve = await SolveAsync(
                captured, position.RaDegrees, position.DecDegrees, cancellationToken).ConfigureAwait(false);

            if (!solve.Success)
            {
                // A failed solve is a failed *capture*, not a failed session:
                // clouds pass, and the sensible response is usually to try the
                // same position again. But it cannot be silent either, so it is
                // its own event and it is bounded -- repeated failures mean
                // something is wrong that retrying will not fix.
                _consecutiveSolveFailures++;
                _events.Publish(new CaptureFailedEvent(
                    _completedCaptures + 1,
                    $"Could not solve ({solve.FailureReason}): {solve.Message}",
                    _consecutiveSolveFailures < MaximumConsecutiveSolveFailures));

                if (_consecutiveSolveFailures >= MaximumConsecutiveSolveFailures)
                {
                    ResetSession();
                    _events.Publish(new SessionFaultedEvent(
                        $"Gave up after {MaximumConsecutiveSolveFailures} consecutive failed solves. " +
                        $"Last failure: {solve.Message}"));
                }

                return;
            }

            _consecutiveSolveFailures = 0;

            PlateSolveSolution solution = solve.Solution!;
            LearnFocalLength(solution);

            // The fit needs the physical pointing direction, so the catalogue
            // position goes back through the full apparent-place transform with
            // refraction included (D15).
            var observer = new ObserverSite(_site!.LatitudeDegrees, _site.LongitudeDegrees, _site.HeightMeters);
            HorizontalCoordinates direction = TopocentricConverter.ToAltAz(
                solution.CenterRaDegrees, solution.CenterDecDegrees, captured.ExposureMidpointUtc,
                observer, _options.EffectiveAtmosphere);

            _observations.Add(direction);
            _completedCaptures++;

            _events.Publish(new PointCapturedEvent(new CapturePoint(
                _completedCaptures, solution.CenterRaDegrees, solution.CenterDecDegrees, captured.ExposureMidpointUtc)));

            if (_observations.Count >= SmallCircleFitter.MinimumObservations)
            {
                PublishEstimate();
            }

            ProposeNext();
        }
        catch (OperationCanceledException)
        {
            ResetSession();
            throw;
        }
        catch (Exception ex)
        {
            // Anything the hardware throws mid-sequence -- a pulled cable, a
            // driver dying -- has to leave a recoverable session rather than an
            // unhandled exception, so the state is torn down and reported.
            ResetSession();
            _events.Publish(new SessionFaultedEvent($"Capture {_completedCaptures + 1} failed: {ex.Message}"));
        }
    }

    private bool EnsureCapturable(out PendingProposal? proposal)
    {
        proposal = _proposal;

        if (!_sessionActive || _plan is null || _site is null)
        {
            _events.Publish(new CommandRejectedEvent("No sequence is running. Start one first."));
            return false;
        }

        if (_camera is null || !_camera.IsConnected || _mount is null || !_mount.IsConnected)
        {
            ResetSession();
            _events.Publish(new SessionFaultedEvent("A device disconnected, so the sequence has been stopped."));
            return false;
        }

        if (proposal is null)
        {
            _events.Publish(new CommandRejectedEvent("There is nothing proposed to capture."));
            return false;
        }

        return true;
    }

    /// <summary>
    /// Whether confirmed coordinates are the proposed ones. One arcsecond, which
    /// is far below anything a user would type deliberately and far above the
    /// rounding introduced by showing six decimal places.
    /// </summary>
    private static bool IsEffectivelyUnchanged(ConfirmSlewCommand command, PendingProposal proposal)
    {
        const double toleranceDegrees = 1.0 / 3600.0;
        return Math.Abs(command.RaDegrees - proposal.RaDegrees) < toleranceDegrees &&
               Math.Abs(command.DecDegrees - proposal.DecDegrees) < toleranceDegrees;
    }

    private static IReadOnlyList<PlannedCapture> ReplaceCapture(
        IReadOnlyList<PlannedCapture> captures, int index, double rotationDegrees)
    {
        var replaced = new List<PlannedCapture>(captures);
        replaced[index] = replaced[index] with { MechanicalRotationDegrees = rotationDegrees };
        return replaced;
    }

    /// <summary>
    /// D8's meridian check, made twice over on purpose. The driver's pier side
    /// is consulted when it is available, and the hour angle is computed
    /// independently from the mount's reported coordinates and the clock --
    /// because some drivers report pier side unreliably (D9), and a flip that
    /// goes unnoticed still returns a confident, meaningless answer.
    /// </summary>
    private async Task<bool> PassesSafetyChecksAsync(MountPosition position, CancellationToken cancellationToken)
    {
        double rotation = TargetSelection.MechanicalRotationOf(
            _site!, position.RaDegrees, position.DecDegrees, DateTime.UtcNow);
        double sign = Math.Sign(rotation);

        PierSide pierSide = position.PierSide;
        if (pierSide == PierSide.Unknown)
        {
            try
            {
                pierSide = await _mount!.GetSideOfPierAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Advisory only (D9): a driver that cannot answer must not stop a
                // sequence, because the computed hour angle carries the check.
            }
        }

        if (_completedCaptures == 0)
        {
            _initialPierSide = pierSide;
            _initialRotationSign = sign;
            return true;
        }

        if (sign != 0 && _initialRotationSign != 0 && sign != _initialRotationSign)
        {
            ResetSession();
            _events.Publish(new AlignmentWithheldEvent(
                "The sequence has crossed the meridian, which inverts the sense of cone error and makes the fit " +
                "meaningless (D8). Restart the sequence on one side."));
            return false;
        }

        if (pierSide != PierSide.Unknown && _initialPierSide != PierSide.Unknown && pierSide != _initialPierSide)
        {
            ResetSession();
            _events.Publish(new AlignmentWithheldEvent(
                $"The mount reports it flipped from {_initialPierSide} to {pierSide} mid-sequence (D8). " +
                "Restart the sequence on one side of the meridian."));
            return false;
        }

        return true;
    }

    private async Task<PlateSolveResult> SolveAsync(
        CapturedImage captured, double hintRa, double hintDec, CancellationToken cancellationToken)
    {
        // A position hint costs nothing here -- the mount has just reported where
        // it thinks it is -- and turns a blind search into a bounded one. The
        // scale hint is only offered once the focal length has actually been
        // measured, since a claimed one is routinely several percent out.
        double? scaleHint = _profile is { IsFocalLengthSolved: true } profile
            ? profile.ExpectedScaleArcsecondsPerPixel
            : null;

        var request = new PlateSolveRequest(
            captured.FitsPath,
            ApproximateScaleArcsecPerPixel: scaleHint,
            ScaleToleranceFraction: scaleHint is null ? null : _profile!.ScaleToleranceFraction,
            ApproximateRaDegrees: hintRa,
            ApproximateDecDegrees: hintDec,
            SearchRadiusDegrees: 10.0,
            Timeout: TimeSpan.FromMinutes(2));

        return await _solver.SolveAsync(request, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Replaces whatever focal length was configured with the one the solve
    /// implies. The point of a profile is that the user supplies a guess once
    /// and the software measures it thereafter.
    /// </summary>
    private void LearnFocalLength(PlateSolveSolution solution)
    {
        if (_profile is null)
        {
            _profile = new EquipmentProfile(
                _camera!.Name, _camera.PixelSizeMicrons, _camera.SensorWidthPixels, _camera.SensorHeightPixels);
        }

        bool wasSolved = _profile.IsFocalLengthSolved;
        double? before = _profile.FocalLengthMillimetres;

        _profile = _profile.WithSolvedScale(solution.PixelScaleArcsecPerPixel);

        // Only announced when it actually changed something, so the log records
        // the moment the scale became known rather than repeating it per capture.
        if (!wasSolved || before is null ||
            Math.Abs(before.Value - _profile.FocalLengthMillimetres!.Value) > 0.5)
        {
            PublishEquipment();
        }
    }

    private void PublishEstimate()
    {
        PolarAlignmentSolution solution = PolarAlignmentSolver.Solve(
            _observations, _site!.LatitudeDegrees, _options.ExpectedSolveNoiseArcseconds);

        if (!solution.IsTrustworthy)
        {
            _events.Publish(new AlignmentWithheldEvent(solution.UntrustworthyReason ?? "The fit could not be trusted."));
            return;
        }

        _events.Publish(new AlignmentUpdatedEvent(new AlignmentEstimate(
            solution.AltitudeErrorArcminutes,
            solution.AzimuthErrorArcminutes,
            solution.TotalErrorArcminutes,
            solution.AltitudeSigmaArcminutes,
            solution.AzimuthSigmaArcminutes,
            solution.TotalSigmaArcminutes,
            solution.Fit.ResidualRmsArcseconds)));
    }

    /// <summary>
    /// Mechanical declination of a sky position, in the mount's own frame. The
    /// companion to <see cref="TargetSelection.MechanicalRotationOf"/>, and
    /// geometric for the same reason: it has to invert exactly what the mount
    /// will do with the coordinates, not model the atmosphere better than the
    /// mount does.
    /// </summary>
    private static double MechanicalDeclinationOf(
        GeodeticLocation site, double raDegrees, double decDegrees, DateTime utc)
    {
        var observer = new ObserverSite(site.LatitudeDegrees, site.LongitudeDegrees, site.HeightMeters);
        HorizontalCoordinates pointing = TopocentricConverter.ToAltAz(
            raDegrees, decDegrees, utc, observer, AtmosphericConditions.Vacuum);

        var (_, declination) = MountMechanics.Decompose(site.LatitudeDegrees, pointing);
        return declination;
    }

    private static MountTrackingState MapTracking(TrackingState state) => state switch
    {
        TrackingState.Tracking => MountTrackingState.Tracking,
        TrackingState.Stopped => MountTrackingState.Stopped,
        _ => MountTrackingState.Unknown,
    };

    private static MeridianSide MapPierSide(PierSide side) => side switch
    {
        PierSide.East => MeridianSide.East,
        PierSide.West => MeridianSide.West,
        _ => MeridianSide.Unknown,
    };

    private static string Describe(DeviceKind kind) => kind == DeviceKind.Camera ? "camera" : "mount";

    private static string DescribeDriver(ConnectDeviceCommand command) =>
        $"{command.ProviderName} / {command.DeviceId}";

    private void ResetSession()
    {
        _sessionActive = false;
        _completedCaptures = 0;
        _consecutiveSolveFailures = 0;
        _initialPierSide = PierSide.Unknown;
        _initialRotationSign = 0;
        _observations.Clear();
        _plan = null;
        _proposal = null;
    }

    public void Dispose()
    {
        if (_ownsCamera)
        {
            _camera?.Dispose();
        }

        if (_ownsMount)
        {
            _mount?.Dispose();
        }

        _commandGate.Dispose();
        _events.Dispose();
    }
}
