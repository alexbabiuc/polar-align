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

/// <param name="ExposureDuration">The exposure the capture loop starts at; <see cref="SetExposureCommand"/> changes it.</param>
/// <param name="ExpectedSolveNoiseArcseconds">
/// Per-observation plate-solve accuracy, which sets the scale of the reported
/// covariance and the yardstick residuals are judged against. Phase 2 measured
/// well under an arcsecond on synthetic frames; the default here is deliberately
/// more pessimistic, since a real sky adds differential refraction, optical
/// distortion and seeing-driven centroid wander.
/// </param>
/// <param name="SettleDelay">
/// How long after a slew ends before a sample is solved (D26). Null is the two
/// seconds decided there; tests shorten it rather than fake the clock.
/// </param>
/// <param name="SolveInterval">
/// How long after a solve's result before the next unconnected solve, and before
/// retrying a failed post-slew one (D26). Null is five seconds.
/// </param>
/// <param name="FailedSolvesDirectory">Where frames whose solve failed are kept (D26). Null is the per-user default.</param>
public sealed record AlignmentSessionOptions(
    int CaptureCount = 6,
    double SweepDegrees = 70.0,
    TimeSpan ExposureDuration = default,
    double ExpectedSolveNoiseArcseconds = 3.0,
    AtmosphericConditions? Atmosphere = null,
    EquipmentProfile? EquipmentProfile = null,
    string? ApplicationName = null,
    string? ApplicationVersion = null,
    TimeSpan? SettleDelay = null,
    TimeSpan? SolveInterval = null,
    string? FailedSolvesDirectory = null)
{
    public TimeSpan EffectiveExposure => ExposureDuration == default ? TimeSpan.FromSeconds(2) : ExposureDuration;

    public TimeSpan EffectiveSettleDelay => SettleDelay ?? TimeSpan.FromSeconds(2);

    public TimeSpan EffectiveSolveInterval => SolveInterval ?? TimeSpan.FromSeconds(5);

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
/// The alignment sequence: connect, confirm where you are, watch the live frame,
/// sample, fit, and report -- with a way out at every step, and with the mount
/// standing still until told otherwise.
///
/// The engine is driven by commands and answers only with events (D6), so the
/// same object serves a local UI now and a remote one later without change. It
/// deliberately exposes no "current state" property: callers reconstruct
/// whatever they need from the event stream, which is what keeps the boundary
/// message-shaped rather than merely message-flavoured.
///
/// Three things run at once -- the capture loop, at most one solve, and at
/// most one slew -- but none of them touches the session's state directly.
/// Each hands its result back through the same gate commands use, so every
/// state change and every published event happens one at a time, in an order a
/// test can reason about. Long work (an exposure, a solve, a slew) always runs
/// outside the gate; only the dialog deliberately holds it (D23).
///
/// Nothing here ever commands motion on its own initiative (D18). Every slew is
/// the direct consequence of a <see cref="CaptureNextPointCommand"/> or
/// <see cref="ConfirmSlewCommand"/> that arrived from outside.
/// </summary>
public sealed class AlignmentSession : IAlignmentEngine, IDisposable
{
    private readonly ISolver _solver;
    private readonly AlignmentSessionOptions _options;
    private readonly DeviceCatalog? _catalog;
    private readonly EventStream _events = new();
    private readonly SemaphoreSlim _commandGate = new(1, 1);

    /// <summary>
    /// Held by the capture loop for the length of each exposure, and by any
    /// command that talks to the camera directly. That is what makes a gain or
    /// readout change land *between* frames rather than halfway through one
    /// (D22, D25), and what lets the driver's window wait for the frame in
    /// progress instead of opening underneath it (D23).
    ///
    /// Lock order is always the command gate, then this. The capture loop takes
    /// this alone and releases it before it asks for the gate, so the two can
    /// never be held against each other.
    /// </summary>
    private readonly SemaphoreSlim _cameraAccess = new(1, 1);

    private readonly FrameStore _frameStore;

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

    /// <summary>
    /// Declination movement seen in the mount's own report that restarts the
    /// sequence (D18 as revised). Above the reporting precision of a mount
    /// standing still; anything smaller that is real is left to D11's residual
    /// check, which sees far less than this.
    /// </summary>
    private const double DeclinationRestartArcminutes = 1.0;

    /// <summary>
    /// Two positions closer than this are the same position: for two solves in a
    /// row on an unconnected mount (D26), and for two polls of a connected one.
    /// Far above solve noise and a mount's reporting jitter, far below any
    /// deliberate move -- the spacing between samples is degrees (D27).
    /// </summary>
    private const double StillToleranceArcminutes = 1.0;

    /// <summary>
    /// The fraction of the planned spacing a move has to cover to count (D27).
    /// Not the whole of it: the engine's own next proposal is exactly one
    /// spacing on, and a mount's report of having arrived there can read a hair
    /// short -- as can the rotation of an unconnected solve, measured about the
    /// nominal pole rather than the real axis. Refusing those would make the
    /// planned sweep unreachable by following the plan.
    /// </summary>
    private const double SpacingAllowance = 0.9;

    private readonly List<HorizontalCoordinates> _observations = new();
    private readonly List<Sample> _samples = new();

    private ICamera? _camera;
    private IMount? _mount;
    private bool _ownsCamera;
    private bool _ownsMount;
    private bool _disposed;

    private CancellationTokenSource? _loopCts;
    private Task? _loopTask;

    /// <summary>Read by the capture loop at every frame boundary, so it is volatile rather than gated.</summary>
    private long _exposureTicks;

    private MountPosition? _latestMountPosition;
    private bool _mountMoving;
    private bool _engineSlewInFlight;

    /// <summary>
    /// The mount is standing where a slew the user confirmed put it. That
    /// position is sampled whatever the spacing (D27): the user saw the proposal
    /// and chose, often because of an obstruction the engine cannot see (D18).
    /// Cleared by any motion the engine did not command, and by the sample.
    /// </summary>
    private bool _atConfirmedPosition;

    private int _captureCount;
    private double _sweepDegrees;
    private double _spacingDegrees;
    private SequenceMode _mode;

    private GeodeticLocation? _site;
    private TargetPlan? _plan;
    private PendingProposal? _proposal;
    private PierSide _initialPierSide = PierSide.Unknown;
    private double _initialRotationSign;
    private bool _meridianWarned;
    private bool _sessionActive;
    private CancellationTokenSource _sessionCts = new();
    private EquipmentProfile? _profile;

    /// <summary>
    /// Bumped whenever the samples are discarded. A solve or slew that finishes
    /// afterwards belongs to a sequence that no longer exists, and is dropped
    /// rather than added to its successor.
    /// </summary>
    private int _generation;

    // ---- Solve scheduling (D26) ----

    /// <summary>A delay counting down towards arming a solve; replaced by any newer trigger.</summary>
    private CancellationTokenSource? _pendingDelay;

    /// <summary>
    /// A solve that is due: the next frame to *start* at or after
    /// <see cref="Armed.NotBeforeUtc"/> is solved, once no other solve is running.
    /// </summary>
    private Armed? _armed;

    private bool _solveInFlight;
    private int _consecutiveSolveFailures;

    /// <summary>The previous unconnected solve, which the next must agree with before either is a sample (D26).</summary>
    private SolvedPosition? _lastUnconnectedSolve;

    private sealed record Armed(SampleTrigger Trigger, DateTime NotBeforeUtc);

    private sealed record Sample(double RotationDegrees, double RaDegrees, double DecDegrees, DateTime MidpointUtc);

    private sealed record SolvedPosition(double RaDegrees, double DecDegrees, HorizontalCoordinates Direction);

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
        _exposureTicks = _options.EffectiveExposure.Ticks;
        _frameStore = new FrameStore(_options.FailedSolvesDirectory ?? FrameStore.DefaultFailedSolvesDirectory);
    }

    /// <summary>
    /// Constructs a session over devices the caller already holds. The devices
    /// count as selected but not yet connected, so the ordinary
    /// connect-then-start sequence still applies -- and the caller keeps
    /// ownership, so the session will not dispose them.
    /// </summary>
    /// <param name="mount">Null for a session that will only ever run unconnected (D10).</param>
    public AlignmentSession(ICamera camera, IMount? mount, ISolver solver, AlignmentSessionOptions? options = null)
    {
        _camera = camera ?? throw new ArgumentNullException(nameof(camera));
        _mount = mount;
        _solver = solver ?? throw new ArgumentNullException(nameof(solver));
        _options = options ?? new AlignmentSessionOptions();
        _profile = _options.EquipmentProfile;
        _exposureTicks = _options.EffectiveExposure.Ticks;
        _frameStore = new FrameStore(_options.FailedSolvesDirectory ?? FrameStore.DefaultFailedSolvesDirectory);
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

    private TimeSpan Exposure => TimeSpan.FromTicks(Interlocked.Read(ref _exposureTicks));

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
                case SetReadoutModeCommand readout:
                    await SetReadoutModeAsync(readout, cancellationToken).ConfigureAwait(false);
                    break;
                case OpenCameraSetupDialogCommand openSetup:
                    await OpenCameraSetupDialogAsync(openSetup, cancellationToken).ConfigureAwait(false);
                    break;
                case SetCameraGainCommand gain:
                    await SetCameraGainAsync(gain, cancellationToken).ConfigureAwait(false);
                    break;
                case SetExposureCommand exposure:
                    SetExposure(exposure);
                    break;
                case StartSessionCommand start:
                    await StartAsync(start, cancellationToken).ConfigureAwait(false);
                    break;
                case RecordSampleCommand:
                    RecordSample();
                    break;
                case CaptureNextPointCommand:
                    CaptureProposed();
                    break;
                case ConfirmSlewCommand confirm:
                    ConfirmSlew(confirm);
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

    /// <summary>
    /// Runs work handed back from the capture loop, a solve or a slew, under the
    /// same gate as a command. False when it never ran -- cancelled while waiting,
    /// or the session has been disposed -- so the caller can clean up after
    /// itself.
    /// </summary>
    private async Task<bool> RunSerialAsync(Func<Task> work, CancellationToken cancellationToken)
    {
        try
        {
            await _commandGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException)
        {
            return false;
        }

        try
        {
            if (_disposed)
            {
                return false;
            }

            await work().ConfigureAwait(false);
            return true;
        }
        finally
        {
            try
            {
                _commandGate.Release();
            }
            catch (ObjectDisposedException)
            {
            }
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
        else
        {
            await StopCaptureLoopAsync().ConfigureAwait(false);
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
            new CameraDescription(
                camera.PixelSizeMicrons,
                camera.SensorWidthPixels,
                camera.SensorHeightPixels,
                camera.ReadoutModes.Select(Describe).ToArray(),
                camera.ReadoutModeIndex,
                camera.HasSetupDialog,
                camera.UniqueId,
                DescribeGain(camera))));

        PublishEquipment();
        _events.Publish(new ExposureChangedEvent(Exposure));

        StartCaptureLoop(camera);
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
            "This session was constructed without a device catalogue or a mount, so it has no mount to connect.");
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
        await StopCaptureLoopAsync().ConfigureAwait(false);

        if (_camera is null)
        {
            return;
        }

        ICamera camera = _camera;
        bool owned = _ownsCamera;
        _ownsCamera = false;

        // As for the mount: a session handed its camera keeps it, so that it can
        // be connected again after it drops out.
        if (_catalog is not null)
        {
            _camera = null;
        }

        // A solve still running on one of this camera's frames keeps its own
        // hold; everything else from it can go now.
        _frameStore.ReleaseAll();

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
        _latestMountPosition = null;
        _mountMoving = false;

        if (_mount is null)
        {
            return;
        }

        IMount mount = _mount;
        bool owned = _ownsMount;
        _ownsMount = false;

        // A session built around devices it was handed keeps the mount it was
        // given, so that it can be connected again; one built from a catalogue
        // opens a fresh one next time.
        if (_catalog is not null)
        {
            _mount = null;
        }

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

    private bool MountConnected => _mount is { IsConnected: true };

    private async Task RefreshMountStatusAsync(CancellationToken cancellationToken)
    {
        if (!MountConnected)
        {
            return;
        }

        try
        {
            MountPosition position = await _mount!.GetPositionAsync(cancellationToken).ConfigureAwait(false);
            await ObservePositionAsync(position, slewJustEnded: false, cancellationToken).ConfigureAwait(false);
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
            if (_sessionActive && _mode != SequenceMode.Unconnected)
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
        if (!MountConnected)
        {
            return null;
        }

        GeodeticLocation reported;
        try
        {
            reported = await _mount!.GetSiteLocationAsync(cancellationToken).ConfigureAwait(false);
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

    // ---- Camera settings ----

    /// <summary>
    /// Changes the exposure every frame from the next one on is taken at. The
    /// frame in progress is left to finish: drivers abort unevenly, and the next
    /// frame is at most a couple of seconds away (D22 as revised).
    /// </summary>
    private void SetExposure(SetExposureCommand command)
    {
        if (command.Duration <= TimeSpan.Zero || command.Duration > TimeSpan.FromMinutes(1))
        {
            _events.Publish(new CommandRejectedEvent(
                $"An exposure has to be longer than zero and no more than a minute; {command.Duration.TotalSeconds:G3} s is not."));
            return;
        }

        Interlocked.Exchange(ref _exposureTicks, command.Duration.Ticks);
        _events.Publish(new ExposureChangedEvent(command.Duration));
    }

    /// <summary>
    /// Selects a readout mode between frames. Allowed during a sequence: the bit
    /// depth changes how noisy a solved position is, not where it is (D22, D25
    /// as revised).
    /// </summary>
    private async Task SetReadoutModeAsync(SetReadoutModeCommand command, CancellationToken cancellationToken)
    {
        if (_camera is null || !_camera.IsConnected)
        {
            _events.Publish(new CommandRejectedEvent("Connect a camera before choosing a readout mode."));
            return;
        }

        if (command.Index < 0 || command.Index >= _camera.ReadoutModes.Count)
        {
            _events.Publish(new CommandRejectedEvent(
                $"This camera offers {_camera.ReadoutModes.Count} readout mode(s); {command.Index} is not one of them."));
            return;
        }

        await _cameraAccess.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await _camera.SetReadoutModeAsync(command.Index, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _events.Publish(new CommandRejectedEvent($"The camera refused that readout mode: {ex.Message}"));
            return;
        }
        finally
        {
            _cameraAccess.Release();
        }

        CameraReadoutMode mode = _camera.ReadoutModes[command.Index];
        _events.Publish(new ReadoutModeChangedEvent(mode.Index, mode.Name, mode.BitDepth));
    }

    /// <summary>
    /// Puts the driver's own settings window in front of the user and waits for
    /// it to close.
    ///
    /// The wait happens inside the command gate, so no other command runs while
    /// the window is open, and inside the camera's own lock, so the capture loop
    /// finishes the frame in progress and then waits. That is deliberate: the
    /// window changes the device the next exposure comes from, and a capture
    /// straddling it would be taken half under the old settings and half under
    /// the new (D23).
    ///
    /// Still refused during a sequence, though no longer for the reason the
    /// readout mode once was: the window can change binning and region of
    /// interest, which change the plate scale the sequence has already measured
    /// (D23 as revised).
    ///
    /// With no camera connected, the one named in the command is opened just for
    /// the window and released afterwards. That is how an ASCOM driver's window
    /// is meant to be used -- it is where the camera is set up before anything
    /// connects to it -- and requiring a connection first would mean connecting
    /// with settings the user was about to change.
    /// </summary>
    private async Task OpenCameraSetupDialogAsync(OpenCameraSetupDialogCommand command, CancellationToken cancellationToken)
    {
        if (_sessionActive)
        {
            _events.Publish(new CommandRejectedEvent(
                "Cannot change driver settings during a sequence. The window can change binning or the region of " +
                "interest, and with them the plate scale the sequence has already measured."));
            return;
        }

        ICamera? target;
        bool transient = false;

        if (_camera is { IsConnected: true })
        {
            target = _camera;
        }
        else if (!string.IsNullOrEmpty(command.ProviderName) && !string.IsNullOrEmpty(command.DeviceId))
        {
            if (_catalog is null)
            {
                // A session built around one attached camera has only that one
                // to configure.
                target = _camera;
            }
            else
            {
                try
                {
                    target = _catalog.OpenCamera(command.ProviderName, command.DeviceId);
                    transient = true;
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _events.Publish(new CommandRejectedEvent(
                        $"Could not open '{command.DeviceId}' to show its driver settings: {ex.Message}"));
                    return;
                }
            }
        }
        else
        {
            target = null;
        }

        if (target is null)
        {
            _events.Publish(new CommandRejectedEvent(
                "Connect a camera, or pick one, before opening its driver settings."));
            return;
        }

        bool holdsCamera = false;
        try
        {
            if (!target.HasSetupDialog)
            {
                _events.Publish(new CommandRejectedEvent(
                    $"'{target.Name}' has no driver settings window to open."));
                return;
            }

            if (!transient)
            {
                await _cameraAccess.WaitAsync(cancellationToken).ConfigureAwait(false);
                holdsCamera = true;
            }

            _events.Publish(new CameraSetupDialogChangedEvent(IsOpen: true));
            try
            {
                await target.ShowSetupDialogAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _events.Publish(new CommandRejectedEvent(
                    $"The camera driver could not show its settings window: {ex.Message}"));
            }
            finally
            {
                // In a finally block because the alternative is an application
                // permanently convinced a window is open and refusing every
                // command for the rest of the session.
                _events.Publish(new CameraSetupDialogChangedEvent(IsOpen: false));
            }
        }
        finally
        {
            if (holdsCamera)
            {
                _cameraAccess.Release();
            }

            if (transient)
            {
                target.Dispose();
            }
        }

        // The window may have changed the readout mode of a connected camera,
        // and the depth this reports is read from whatever mode is now current.
        if (!transient)
        {
            PublishReadoutMode();
        }
    }

    /// <summary>
    /// Sets the connected camera's gain from a percentage of its own range,
    /// between frames, and reports what the camera actually took. Allowed during
    /// a sequence (D25 as revised).
    /// </summary>
    private async Task SetCameraGainAsync(SetCameraGainCommand command, CancellationToken cancellationToken)
    {
        if (_camera is null || !_camera.IsConnected)
        {
            _events.Publish(new CommandRejectedEvent("Connect a camera before setting its gain."));
            return;
        }

        if (_camera.GainRange is not { } range)
        {
            _events.Publish(new CommandRejectedEvent(
                $"'{_camera.Name}' does not have its gain set from here. For an ASCOM camera, use its driver settings."));
            return;
        }

        if (command.Percent is < GainScale.MinimumPercent or > GainScale.MaximumPercent)
        {
            _events.Publish(new CommandRejectedEvent(
                $"Gain is set as a percentage of the camera's range; {command.Percent} is outside 0 to 100."));
            return;
        }

        int requested = GainScale.ToRaw(command.Percent, range);

        await _cameraAccess.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await _camera.SetGainAsync(requested, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _events.Publish(new CommandRejectedEvent($"The camera refused that gain: {ex.Message}"));
            return;
        }
        finally
        {
            _cameraAccess.Release();
        }

        if (DescribeGain(_camera, command.Percent) is { } applied)
        {
            _events.Publish(new CameraGainChangedEvent(applied));
        }
    }

    /// <summary>
    /// The camera's gain as the UI shows it, read back from the camera.
    ///
    /// When the camera took exactly what the requested percentage maps to, the
    /// requested percentage is reported as-is. Converting the raw value back
    /// would be correct too, but on a narrow range several percentages share one
    /// raw value, and the control would jump from the number the user typed to a
    /// neighbouring one for no reason they could see.
    /// </summary>
    private static CameraGainDescription? DescribeGain(ICamera camera, int? requestedPercent = null)
    {
        if (camera.GainRange is not { } range || camera.Gain is not { } value)
        {
            return null;
        }

        int percent = requestedPercent is { } asked && GainScale.ToRaw(asked, range) == value
            ? asked
            : GainScale.ToPercent(value, range);

        return new CameraGainDescription(percent, value, range.Minimum, range.Maximum);
    }

    /// <summary>Re-reports the camera's current readout mode, for when something outside this class may have changed it.</summary>
    private void PublishReadoutMode()
    {
        if (_camera?.ReadoutModeIndex is not { } index ||
            index < 0 || index >= _camera.ReadoutModes.Count)
        {
            return;
        }

        CameraReadoutMode mode = _camera.ReadoutModes[index];
        _events.Publish(new ReadoutModeChangedEvent(mode.Index, mode.Name, mode.BitDepth));
    }

    private static CameraReadoutModeDescription Describe(CameraReadoutMode mode) =>
        new(mode.Index, mode.Name, mode.BitDepth);

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

    // ---- The capture loop (D26) ----

    private void StartCaptureLoop(ICamera camera)
    {
        var cts = new CancellationTokenSource();
        _loopCts = cts;
        _loopTask = Task.Run(() => CaptureLoopAsync(camera, cts.Token));
    }

    /// <summary>
    /// Stops the loop and waits for it. Safe to call under the gate: the loop
    /// only ever waits for the gate with its own token, so cancelling it lets
    /// it go rather than leaving the two waiting on each other.
    /// </summary>
    private async Task StopCaptureLoopAsync()
    {
        CancellationTokenSource? cts = _loopCts;
        Task? task = _loopTask;
        _loopCts = null;
        _loopTask = null;

        if (cts is null)
        {
            return;
        }

        cts.Cancel();
        if (task is not null)
        {
            try
            {
                await task.ConfigureAwait(false);
            }
            catch (Exception)
            {
                // The loop reports its own failures; nothing is left to do here.
            }
        }

        cts.Dispose();
    }

    /// <summary>
    /// Exposes, publishes, repeats -- for as long as the camera is connected,
    /// sequence or not. Settings are read at every frame boundary, which is the
    /// whole reason exposure and gain can now change at any time.
    /// </summary>
    private async Task CaptureLoopAsync(ICamera camera, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            CapturedImage frame;
            TimeSpan exposure;
            DateTime startedUtc;
            int? gain;

            try
            {
                await _cameraAccess.WaitAsync(cancellationToken).ConfigureAwait(false);
                try
                {
                    exposure = Exposure;
                    startedUtc = DateTime.UtcNow;
                    frame = await camera.ExposeAsync(exposure, BuildCaptureContext(), cancellationToken)
                        .ConfigureAwait(false);

                    // Read before the camera is let go, so a gain change waiting
                    // on this lock cannot be credited to the frame before it.
                    gain = camera.GainRange is not null ? camera.Gain : null;
                }
                finally
                {
                    _cameraAccess.Release();
                }
            }
            catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException &&
                                       cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                await RunSerialAsync(() => OnCameraFailedAsync(camera, ex), CancellationToken.None).ConfigureAwait(false);
                return;
            }

            try
            {
                // A camera that hands a frame back faster than the exposure it
                // was asked for -- a stub, or a driver returning a cached frame --
                // would otherwise spin the loop flat out.
                TimeSpan elapsed = DateTime.UtcNow - startedUtc;
                if (elapsed < exposure)
                {
                    await Task.Delay(exposure - elapsed, cancellationToken).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException)
            {
                TryDelete(frame.FitsPath);
                return;
            }

            if (!await RunSerialAsync(() => OnFrameAsync(camera, frame, startedUtc, gain), cancellationToken).ConfigureAwait(false))
            {
                TryDelete(frame.FitsPath);
                return;
            }
        }
    }

    /// <summary>
    /// What the session knows and the camera does not, for the frame's header
    /// (D20). Read from the capture loop's thread, so it takes one snapshot of
    /// each field rather than reading them twice.
    /// </summary>
    private CaptureContext BuildCaptureContext()
    {
        MountPosition? position = _latestMountPosition;
        EquipmentProfile? profile = _profile;
        return new CaptureContext(
            _options.ApplicationName,
            _options.ApplicationVersion,
            position?.RaDegrees,
            position?.DecDegrees,
            profile?.FocalLengthMillimetres);
    }

    private Task OnFrameAsync(ICamera camera, CapturedImage frame, DateTime startedUtc, int? gain)
    {
        if (!ReferenceEquals(camera, _camera))
        {
            TryDelete(frame.FitsPath);
            return Task.CompletedTask;
        }

        _events.Publish(new FrameCapturedEvent(frame.FitsPath, frame.ExposureMidpointUtc, frame.Duration, gain));
        _frameStore.Displayed(frame.FitsPath);

        if (_sessionActive && !_solveInFlight && _armed is { } armed && startedUtc >= armed.NotBeforeUtc)
        {
            StartSolve(armed.Trigger, frame);
        }

        return Task.CompletedTask;
    }

    private async Task OnCameraFailedAsync(ICamera camera, Exception error)
    {
        if (!ReferenceEquals(camera, _camera))
        {
            return;
        }

        // This runs on the loop itself, so the loop must not be waited for.
        _loopCts?.Dispose();
        _loopCts = null;
        _loopTask = null;

        if (_sessionActive)
        {
            ResetSession();
            _events.Publish(new SessionFaultedEvent($"The camera stopped responding: {error.Message}"));
        }

        _events.Publish(new DeviceDisconnectedEvent(DeviceKind.Camera, $"The camera stopped responding: {error.Message}"));
        await ReleaseCameraAsync(CancellationToken.None).ConfigureAwait(false);
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception)
        {
            // A frame that cannot be deleted is litter in a temp directory, not
            // a reason to stop capturing.
        }
    }

    // ---- Starting a sequence ----

    private async Task StartAsync(StartSessionCommand start, CancellationToken cancellationToken)
    {
        ResetSession();

        if (_camera is null || !_camera.IsConnected)
        {
            _events.Publish(new CommandRejectedEvent("Connect a camera before starting a sequence."));
            return;
        }

        if (_site is null)
        {
            _events.Publish(new CommandRejectedEvent(
                "Confirm the observing site before starting a sequence. The latitude in particular goes straight " +
                "into the answer (D19), so it is not something to be inherited silently."));
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

        if (_captureCount < SmallCircleFitter.MinimumObservations)
        {
            _events.Publish(new CommandRejectedEvent(
                $"A sequence needs at least {SmallCircleFitter.MinimumObservations} captures (D7); " +
                $"{_captureCount} was requested."));
            return;
        }

        // D10 as revised: the mode follows what is connected.
        _mode = !MountConnected
            ? SequenceMode.Unconnected
            : _mount!.CanSlewAsync ? SequenceMode.Driven : SequenceMode.Observed;

        try
        {
            TargetPlan plan;
            MountPosition? position = null;

            if (_mode == SequenceMode.Unconnected)
            {
                plan = TargetSelection.Plan(
                    _site, DateTime.UtcNow, _captureCount, _sweepDegrees, _options.EffectiveAtmosphere);
            }
            else
            {
                // Where the mount believes it is. Its belief, not a solved
                // position, is the right anchor: the mechanical declination the
                // sequence must hold constant is defined in the frame the mount
                // drives in.
                position = await _mount!.GetPositionAsync(cancellationToken).ConfigureAwait(false);
                double rotation = TargetSelection.MechanicalRotationOf(
                    _site, position.RaDegrees, position.DecDegrees, DateTime.UtcNow);
                double declination = MechanicalDeclinationOf(_site, position.RaDegrees, position.DecDegrees, DateTime.UtcNow);

                plan = TargetSelection.PlanFrom(
                    _site, rotation, declination, DateTime.UtcNow,
                    _captureCount, _sweepDegrees, _options.EffectiveAtmosphere);
            }

            if (!plan.Success)
            {
                _events.Publish(new SessionFaultedEvent(plan.Reason ?? "No usable target could be selected."));
                return;
            }

            _plan = plan;
            _spacingDegrees = SpacingOf(plan);
            _sessionActive = true;

            _events.Publish(new SessionStartedEvent(start.Configuration, _mode));
            _events.Publish(new TargetSelectedEvent(
                plan.MechanicalDeclinationDegrees, plan.IsWest, plan.Captures.Count, plan.SweepDegrees));

            ProposeNext();

            if (_mode == SequenceMode.Unconnected)
            {
                // Solving starts straight away. The user is probably still
                // setting the telescope up, which is exactly what the
                // two-solves-agree rule is for (D26).
                ScheduleSolve(SampleTrigger.Periodic, TimeSpan.Zero);
            }
            else
            {
                await ObservePositionAsync(position!, slewJustEnded: false, cancellationToken).ConfigureAwait(false);
                if (_sessionActive && !_mountMoving && _plan.AnchoredAtCurrentPointing)
                {
                    // Nothing needs to move and nothing is moving: the first
                    // sample is taken where the telescope stands, with no click
                    // (D18 as revised).
                    ScheduleSolve(SampleTrigger.SlewEnded, _options.EffectiveSettleDelay);
                }
            }
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
    /// The least distance, in mechanical rotation, between two automatically
    /// triggered samples (D27). A plan of one capture has no spacing, and then
    /// the whole sweep is the only distance that means anything.
    /// </summary>
    private static double SpacingOf(TargetPlan plan) =>
        plan.SampleSpacingDegrees > 0.0 ? plan.SampleSpacingDegrees : plan.SweepDegrees;

    // ---- Proposals ----

    /// <summary>
    /// Works out and announces the next point, then stops. This is the whole of
    /// D18: the engine's response to a sample is a suggestion, never a movement.
    ///
    /// The first point is the plan's. Every later one is the westernmost sample
    /// so far plus the spacing, rather than the plan's next entry, because a
    /// mount moved from a hand controller or by hand is sampled wherever it
    /// stopped, and a proposal for a position already passed is not a proposal.
    /// Sequences sweep west (D18 as revised), so "further" is always west.
    /// </summary>
    private void ProposeNext()
    {
        if (_plan is null || _site is null)
        {
            return;
        }

        if (_samples.Count >= _captureCount)
        {
            ResetSession();
            _events.Publish(new SessionCompletedEvent());
            return;
        }

        double rotation;
        double predictedAltitude;

        if (_samples.Count == 0 || _mode == SequenceMode.Unconnected)
        {
            // Unconnected, the plan's own points are the instructions all the
            // way through. The rotation of an unconnected sample is measured
            // about the nominal pole, so it is off by the very misalignment
            // being measured (D27), and stepping on from it walks the last
            // point across the meridian margin the plan was built to respect.
            // Only the turn between points is meaningful to someone doing it
            // by hand, and that the plan already gets right.
            PlannedCapture planned = _plan.Captures[Math.Min(_samples.Count, _plan.Captures.Count - 1)];
            rotation = planned.MechanicalRotationDegrees;
            predictedAltitude = planned.PredictedAltitudeDegrees;
        }
        else
        {
            double westmost = _samples.Max(s => s.RotationDegrees);
            rotation = westmost + _spacingDegrees;

            // An eastern sweep ends at the meridian margin (D8), and its last
            // planned point sits exactly on it -- so a step that would cross
            // stops there instead, if that still widens the sweep enough.
            if (_samples[0].RotationDegrees < 0.0 && rotation > -TargetSelection.MeridianMarginDegrees)
            {
                rotation = -TargetSelection.MeridianMarginDegrees;
            }

            predictedAltitude = PredictedAltitude(rotation);

            if (rotation - westmost < _spacingDegrees * SpacingAllowance || !IsReachable(rotation, predictedAltitude))
            {
                string why =
                    $"The sweep cannot go further west from here without coming within " +
                    $"{TargetSelection.MeridianMarginDegrees:F0}° of the meridian or dropping below " +
                    $"{TargetSelection.MinimumAltitudeDegrees:F0}° (D8).";

                if (_samples.Count >= SmallCircleFitter.MinimumObservations)
                {
                    _events.Publish(new ManualActionRequiredEvent(
                        $"{why} The sequence ends with the {_samples.Count} samples it has.", rotation));
                    ResetSession();
                    _events.Publish(new SessionCompletedEvent());
                }
                else
                {
                    _proposal = null;
                    _events.Publish(new ManualActionRequiredEvent(
                        $"{why} Record samples between the ones already taken, or stop and start again further east.",
                        rotation));
                }

                return;
            }
        }

        int pointIndex = _samples.Count + 1;
        bool requiresMotion = _mode == SequenceMode.Driven &&
                              !(_samples.Count == 0 && _plan.AnchoredAtCurrentPointing);

        (double ra, double dec) = TargetSelection.ResolveCommand(
            _site, rotation, _plan.MechanicalDeclinationDegrees, DateTime.UtcNow);

        _proposal = new PendingProposal(pointIndex, rotation, ra, dec, requiresMotion);

        string instruction = BuildInstruction(rotation, predictedAltitude, pointIndex, requiresMotion);
        _events.Publish(new SlewProposedEvent(
            pointIndex, _captureCount, ra, dec, rotation, predictedAltitude, requiresMotion, instruction));

        if (_mode != SequenceMode.Driven)
        {
            // D10's own event as well, so a UI built around manual mounts can
            // present the instruction without having to interpret a proposal
            // that it knows will never involve a slew.
            _events.Publish(new ManualActionRequiredEvent(instruction, rotation));
        }
    }

    private double PredictedAltitude(double rotation) =>
        MountMechanics.Compose(
            _site!.LatitudeDegrees, MountMisalignment.Aligned, rotation, _plan!.MechanicalDeclinationDegrees)
            .AltitudeDegrees;

    /// <summary>On the side the sequence started on, clear of the meridian margin, and above the altitude floor (D8).</summary>
    private bool IsReachable(double rotation, double predictedAltitude)
    {
        double side = _samples.Count > 0 ? Math.Sign(_samples[0].RotationDegrees) : Math.Sign(rotation);
        return Math.Sign(rotation) == side &&
               Math.Abs(rotation) >= TargetSelection.MeridianMarginDegrees &&
               predictedAltitude >= TargetSelection.MinimumAltitudeDegrees;
    }

    private string BuildInstruction(double rotation, double predictedAltitude, int pointIndex, bool requiresMotion)
    {
        string side = rotation >= 0 ? "west" : "east";
        string where = $"{Math.Abs(rotation):F0}° {side} of the meridian, predicted altitude {predictedAltitude:F0}°";
        string prefix = $"Point {pointIndex} of {_captureCount}:";

        switch (_mode)
        {
            case SequenceMode.Unconnected when pointIndex == 1:
                // Sky declination for the user to set: mechanical declination is
                // measured towards the visible pole, which is the south pole
                // below the equator (see TargetSelection.Plan).
                double skyDeclination = _site!.LatitudeDegrees >= 0
                    ? _plan!.MechanicalDeclinationDegrees
                    : -_plan!.MechanicalDeclinationDegrees;
                return $"{prefix} set the declination axis to about {skyDeclination:F0}° and lock it -- from " +
                       "here on, do not touch declination, because the whole measurement depends on it staying " +
                       $"where it is (D11). Then turn the mount in RA to about {where}, keeping the counterweight " +
                       "down. Each sample is taken once two solves in a row agree the telescope is still.";

            case SequenceMode.Unconnected:
                return $"{prefix} turn the mount about {_spacingDegrees:F0}° west in RA, to about {where}. " +
                       "Leave declination alone. The sample is taken once the telescope is still.";

            case SequenceMode.Observed:
                return $"{prefix} turn the mount in RA with its hand controller to about {where}, without " +
                       "touching declination. The sample is taken when it stops.";
        }

        if (!requiresMotion)
        {
            return $"{prefix} the telescope is already somewhere usable ({where}). It is sampled where it " +
                   "stands once it is still -- nothing needs to move, and this frame is what measures the focal " +
                   "length for every solve after it.";
        }

        return $"{prefix} slew to {where}. Check the way is clear, then confirm. Edit the coordinates if that " +
               "part of the sky is blocked. Moving it from the hand controller instead works too.";
    }

    // ---- Commands that move the mount, or ask for a sample ----

    private bool EnsureSequence()
    {
        if (!_sessionActive || _plan is null || _site is null)
        {
            _events.Publish(new CommandRejectedEvent("No sequence is running. Start one first."));
            return false;
        }

        if (_camera is null || !_camera.IsConnected ||
            (_mode != SequenceMode.Unconnected && !MountConnected))
        {
            ResetSession();
            _events.Publish(new SessionFaultedEvent("A device disconnected, so the sequence has been stopped."));
            return false;
        }

        return true;
    }

    private bool EnsureSlewable(out PendingProposal? proposal)
    {
        proposal = _proposal;

        if (!EnsureSequence())
        {
            return false;
        }

        if (_mode != SequenceMode.Driven)
        {
            _events.Publish(new CommandRejectedEvent(_mode == SequenceMode.Unconnected
                ? "No mount is connected, so the engine cannot slew. Turn the telescope by hand as instructed."
                : "This mount cannot slew on command (D9). Turn it with its hand controller as instructed."));
            return false;
        }

        if (_engineSlewInFlight)
        {
            _events.Publish(new CommandRejectedEvent("The mount is already slewing. Wait for it to arrive."));
            return false;
        }

        if (proposal is null)
        {
            _events.Publish(new CommandRejectedEvent("There is nothing proposed to slew to."));
            return false;
        }

        return true;
    }

    private void RecordSample()
    {
        if (!EnsureSequence())
        {
            return;
        }

        // The forced sample replaces whatever was counting down, and waits for a
        // frame that starts from now: the one on screen may have been exposed
        // while the telescope was still moving (D26).
        CancelPendingSolve();
        _armed = new Armed(SampleTrigger.Forced, DateTime.UtcNow);
        _events.Publish(new SolveScheduledEvent(SampleTrigger.Forced, TimeSpan.Zero));
    }

    private void CaptureProposed()
    {
        if (!EnsureSlewable(out PendingProposal? proposal))
        {
            return;
        }

        if (!proposal!.RequiresMotion)
        {
            _events.Publish(new CommandRejectedEvent(
                "Nothing needs to move: the sample is taken where the telescope stands once it is still. " +
                "Record a sample to take it now."));
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
        BeginSlew(ra, dec);
    }

    private void ConfirmSlew(ConfirmSlewCommand command)
    {
        if (!EnsureSlewable(out PendingProposal? proposal))
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
            CaptureProposed();
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
            string reason =
                $"Those coordinates sit {declinationDifferenceArcminutes:F0}' away in declination, which is a " +
                "different target rather than a different hour angle on the same one. Points at different " +
                "declinations do not lie on one circle about the polar axis, so the earlier captures have been " +
                "discarded and the sequence restarted here.";

            if (!Reanchor(rotation, declination, reason, moved: true))
            {
                return;
            }

            _events.Publish(new SlewConfirmedEvent(
                command.RaDegrees, command.DecDegrees, WasOverridden: true, ReanchoredReason: reason));
            BeginSlew(command.RaDegrees, command.DecDegrees);
            return;
        }

        // Same target, different hour angle. The declination is snapped back to
        // the sequence's exactly: leaving it a few arcminutes out would put a
        // real declination movement into the data, which is precisely what the
        // sequence must not contain.
        (double ra, double dec) = TargetSelection.ResolveCommand(
            _site!, rotation, _plan.MechanicalDeclinationDegrees, DateTime.UtcNow);

        _proposal = proposal! with { MechanicalRotationDegrees = rotation, RaDegrees = ra, DecDegrees = dec };

        _events.Publish(new SlewConfirmedEvent(ra, dec, WasOverridden: true));
        BeginSlew(ra, dec);
    }

    /// <summary>
    /// Throws away the samples taken so far and re-plans from a new position.
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

        ClearSamples();

        // The new plan is anchored where the telescope is going, or already is,
        // whatever the planner would have preferred: the user chose it (D18).
        _plan = plan with
        {
            Captures = new[] { new PlannedCapture(rotation, PredictedAltitudeAt(rotation, declination)) }
                .Concat(plan.Captures.Skip(1)).ToArray(),
            MechanicalDeclinationDegrees = declination,
            AnchoredAtCurrentPointing = !moved,
        };
        _spacingDegrees = SpacingOf(plan);

        _events.Publish(new AlignmentWithheldEvent(reason));
        _events.Publish(new TargetSelectedEvent(
            declination, plan.IsWest, plan.Captures.Count, plan.SweepDegrees));

        _proposal = new PendingProposal(1, rotation, 0.0, 0.0, RequiresMotion: moved);
        return true;
    }

    private double PredictedAltitudeAt(double rotation, double declination) =>
        MountMechanics.Compose(_site!.LatitudeDegrees, MountMisalignment.Aligned, rotation, declination).AltitudeDegrees;

    private void BeginSlew(double ra, double dec)
    {
        IMount mount = _mount!;
        int generation = _generation;
        CancellationToken token = _sessionCts.Token;

        _engineSlewInFlight = true;
        _mountMoving = true;
        CancelPendingSolve();

        _ = Task.Run(async () =>
        {
            Exception? error = null;
            try
            {
                await mount.SlewToCoordinatesAsync(ra, dec, token).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                error = ex;
            }

            await RunSerialAsync(() => OnEngineSlewFinishedAsync(mount, generation, error), CancellationToken.None)
                .ConfigureAwait(false);
        });
    }

    private async Task OnEngineSlewFinishedAsync(IMount mount, int generation, Exception? error)
    {
        if (!ReferenceEquals(mount, _mount))
        {
            return;
        }

        _engineSlewInFlight = false;

        if (error is OperationCanceledException || !_sessionActive)
        {
            return;
        }

        if (error is not null)
        {
            // Anything the hardware throws mid-sequence -- a pulled cable, a
            // driver dying -- has to leave a recoverable session rather than an
            // unhandled exception, so the state is torn down and reported.
            ResetSession();
            _events.Publish(new SessionFaultedEvent($"The slew to point {_samples.Count + 1} failed: {error.Message}"));
            return;
        }

        try
        {
            MountPosition position = await mount.GetPositionAsync(_sessionCts.Token).ConfigureAwait(false);
            _atConfirmedPosition = generation == _generation;
            await ObservePositionAsync(position, slewJustEnded: _atConfirmedPosition, _sessionCts.Token)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            ResetSession();
            _events.Publish(new SessionFaultedEvent($"Lost contact with the mount after a slew: {ex.Message}"));
        }
    }

    // ---- Watching the mount (D26) ----

    /// <summary>
    /// Takes in a fresh report of where the mount is, and reacts to what changed:
    /// motion cancels a solve that was counting down, the end of motion starts
    /// one, and declination movement restarts the sequence.
    /// </summary>
    /// <param name="slewJustEnded">
    /// The engine's own slew has just returned, so the mount has arrived even
    /// though its position differs from the last poll -- which would otherwise
    /// read as still moving.
    /// </param>
    private async Task ObservePositionAsync(MountPosition position, bool slewJustEnded, CancellationToken cancellationToken)
    {
        bool moved = !slewJustEnded && _latestMountPosition is { } previous && HasMoved(previous, position);
        bool moving = position.IsSlewing || moved || _engineSlewInFlight;
        bool wasMoving = _mountMoving || slewJustEnded;

        _latestMountPosition = position;
        _mountMoving = moving;

        _events.Publish(new MountStatusEvent(
            position.RaDegrees,
            position.DecDegrees,
            MapTracking(position.Tracking),
            MapPierSide(position.PierSide),
            moving));

        if (!_sessionActive || _mode == SequenceMode.Unconnected)
        {
            return;
        }

        if (moving)
        {
            if (!_engineSlewInFlight)
            {
                _atConfirmedPosition = false;
            }

            if (!wasMoving)
            {
                CancelPendingSolve();
            }

            return;
        }

        if (await RestartedOnDeclinationAsync(position, cancellationToken).ConfigureAwait(false))
        {
            return;
        }

        if (wasMoving)
        {
            OnSlewEnded(position);
        }
    }

    /// <summary>
    /// Still in either frame is still. A tracking mount holds its sky
    /// coordinates while its mechanical rotation turns; a mount with the drive
    /// off holds its mechanical angles while its sky coordinates drift at the
    /// sidereal rate. Moving means neither held.
    /// </summary>
    private bool HasMoved(MountPosition previous, MountPosition current)
    {
        double sky = SeparationDegrees(previous.RaDegrees, previous.DecDegrees, current.RaDegrees, current.DecDegrees);
        if (sky * 60.0 <= StillToleranceArcminutes || _site is null)
        {
            return sky * 60.0 > StillToleranceArcminutes;
        }

        double rotationBefore = TargetSelection.MechanicalRotationOf(
            _site, previous.RaDegrees, previous.DecDegrees, previous.TimestampUtc);
        double rotationAfter = TargetSelection.MechanicalRotationOf(
            _site, current.RaDegrees, current.DecDegrees, current.TimestampUtc);
        double declinationBefore = MechanicalDeclinationOf(
            _site, previous.RaDegrees, previous.DecDegrees, previous.TimestampUtc);
        double declinationAfter = MechanicalDeclinationOf(
            _site, current.RaDegrees, current.DecDegrees, current.TimestampUtc);

        double rotationChange = WrapDegrees(rotationAfter - rotationBefore) *
                                Math.Cos(declinationAfter * Math.PI / 180.0);
        double mechanical = Math.Sqrt(rotationChange * rotationChange +
                                      Math.Pow(declinationAfter - declinationBefore, 2));
        return mechanical * 60.0 > StillToleranceArcminutes;
    }

    private void OnSlewEnded(MountPosition position)
    {
        double rotation = TargetSelection.MechanicalRotationOf(
            _site!, position.RaDegrees, position.DecDegrees, DateTime.UtcNow);

        if (!_atConfirmedPosition && IsTooClose(rotation, out double nearest))
        {
            _events.Publish(new SampleSkippedEvent(
                $"The mount has turned {nearest:F1}° from the nearest sample; samples need to be at least " +
                $"{_spacingDegrees:F0}° apart to widen the sweep (D27). Turn further, or record a sample to use " +
                "this position anyway."));
            return;
        }

        ScheduleSolve(SampleTrigger.SlewEnded, _options.EffectiveSettleDelay);
    }

    /// <summary>Whether a position is too near an existing sample to widen the sweep (D27).</summary>
    private bool IsTooClose(double rotation, out double nearestDegrees)
    {
        nearestDegrees = _samples.Count == 0
            ? double.PositiveInfinity
            : _samples.Min(s => Math.Abs(WrapDegrees(s.RotationDegrees - rotation)));
        return nearestDegrees < _spacingDegrees * SpacingAllowance;
    }

    /// <summary>
    /// D18 as revised: declination movement on a connected mount restarts the
    /// sequence, because the samples already taken lie on a different circle and
    /// no decision about them is left for the user to make. Before the first
    /// sample there is nothing to discard, so the plan simply follows the mount.
    /// </summary>
    private async Task<bool> RestartedOnDeclinationAsync(MountPosition position, CancellationToken cancellationToken)
    {
        if (_plan is null)
        {
            return false;
        }

        double declination = MechanicalDeclinationOf(_site!, position.RaDegrees, position.DecDegrees, DateTime.UtcNow);
        double changeArcminutes = Math.Abs(declination - _plan.MechanicalDeclinationDegrees) * 60.0;

        if (_samples.Count == 0)
        {
            // An unanchored plan is waiting for the mount to go somewhere else;
            // where it is now says nothing about the declination it will hold.
            if (_plan.AnchoredAtCurrentPointing && changeArcminutes > DeclinationRestartArcminutes)
            {
                _plan = _plan with { MechanicalDeclinationDegrees = declination };
                _events.Publish(new TargetSelectedEvent(
                    declination, _plan.IsWest, _plan.Captures.Count, _plan.SweepDegrees));
            }

            return false;
        }

        if (changeArcminutes <= DeclinationRestartArcminutes)
        {
            return false;
        }

        double rotation = TargetSelection.MechanicalRotationOf(
            _site!, position.RaDegrees, position.DecDegrees, DateTime.UtcNow);

        string reason =
            $"The mount moved {changeArcminutes:F1}' in declination. The {_samples.Count} sample(s) taken so far " +
            "lie on a different circle about the polar axis and cannot be combined with new ones, so they have " +
            "been discarded and the sequence restarted here (D18). Leave declination alone from now on.";

        TargetPlan plan = TargetSelection.PlanFrom(
            _site!, rotation, declination, DateTime.UtcNow, _captureCount, _sweepDegrees, _options.EffectiveAtmosphere);

        if (!plan.Success)
        {
            ResetSession();
            _events.Publish(new SessionFaultedEvent($"{reason} No usable sequence can be planned from there: {plan.Reason}"));
            return true;
        }

        ClearSamples();
        _plan = plan;
        _spacingDegrees = SpacingOf(plan);

        _events.Publish(new SequenceRestartedEvent(reason));
        _events.Publish(new TargetSelectedEvent(
            plan.MechanicalDeclinationDegrees, plan.IsWest, plan.Captures.Count, plan.SweepDegrees));
        ProposeNext();

        if (plan.AnchoredAtCurrentPointing)
        {
            ScheduleSolve(SampleTrigger.SlewEnded, _options.EffectiveSettleDelay);
        }

        await Task.CompletedTask.ConfigureAwait(false);
        return true;
    }

    // ---- Scheduling and running solves (D26) ----

    /// <summary>
    /// Arms a solve after <paramref name="delay"/>, replacing anything already
    /// counting down. A forced sample already waiting is left alone: the user
    /// asked for it, and a later automatic trigger is not a reason to drop it.
    /// </summary>
    private void ScheduleSolve(SampleTrigger trigger, TimeSpan delay)
    {
        if (_armed is { Trigger: SampleTrigger.Forced })
        {
            return;
        }

        CancelPendingSolve();

        var delayCts = CancellationTokenSource.CreateLinkedTokenSource(_sessionCts.Token);
        _pendingDelay = delayCts;
        int generation = _generation;

        _events.Publish(new SolveScheduledEvent(trigger, delay));

        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(delay, delayCts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            await RunSerialAsync(() =>
            {
                if (ReferenceEquals(_pendingDelay, delayCts) && generation == _generation && _sessionActive)
                {
                    _pendingDelay = null;
                    delayCts.Dispose();
                    _armed = new Armed(trigger, DateTime.UtcNow);
                }

                return Task.CompletedTask;
            }, CancellationToken.None).ConfigureAwait(false);
        });
    }

    /// <summary>Drops a solve counting down or armed, except one the user forced.</summary>
    private void CancelPendingSolve()
    {
        if (_pendingDelay is { } pending)
        {
            _pendingDelay = null;
            pending.Cancel();
        }

        if (_armed is { Trigger: not SampleTrigger.Forced })
        {
            _armed = null;
        }
    }

    private void StartSolve(SampleTrigger trigger, CapturedImage frame)
    {
        _armed = null;
        _solveInFlight = true;

        IDisposable hold = _frameStore.Hold(frame.FitsPath);
        int generation = _generation;
        CancellationToken token = _sessionCts.Token;

        // An unconnected solve is blind by nature; a connected one is hinted
        // with where the mount believes it points, which costs nothing and turns
        // a blind search into a bounded one.
        MountPosition? hint = _mode == SequenceMode.Unconnected ? null : _latestMountPosition;
        PlateSolveRequest request = BuildSolveRequest(frame, hint);

        _events.Publish(new SolveStartedEvent(trigger, frame.FitsPath));

        _ = Task.Run(async () =>
        {
            PlateSolveResult result;
            try
            {
                result = await _solver.SolveAsync(request, token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                result = PlateSolveResult.Failed(PlateSolveFailureReason.Cancelled, "The sequence was stopped.");
            }
            catch (Exception ex)
            {
                result = PlateSolveResult.Failed(PlateSolveFailureReason.SolverError, ex.Message);
            }

            bool ran = await RunSerialAsync(
                () => OnSolveResultAsync(generation, trigger, frame, result, hold), CancellationToken.None)
                .ConfigureAwait(false);

            if (!ran)
            {
                hold.Dispose();
            }
        });
    }

    private PlateSolveRequest BuildSolveRequest(CapturedImage frame, MountPosition? hint)
    {
        // The scale hint is only offered once the focal length has actually
        // been measured, since a claimed one is routinely several percent out.
        double? scaleHint = _profile is { IsFocalLengthSolved: true } profile
            ? profile.ExpectedScaleArcsecondsPerPixel
            : null;

        return new PlateSolveRequest(
            frame.FitsPath,
            ApproximateScaleArcsecPerPixel: scaleHint,
            ScaleToleranceFraction: scaleHint is null ? null : _profile!.ScaleToleranceFraction,
            ApproximateRaDegrees: hint?.RaDegrees,
            ApproximateDecDegrees: hint?.DecDegrees,
            SearchRadiusDegrees: hint is null ? null : 10.0,
            Timeout: TimeSpan.FromMinutes(2));
    }

    private async Task OnSolveResultAsync(
        int generation, SampleTrigger trigger, CapturedImage frame, PlateSolveResult result, IDisposable hold)
    {
        _solveInFlight = false;

        try
        {
            if (generation != _generation || !_sessionActive)
            {
                return;
            }

            if (!result.Success)
            {
                OnSolveFailed(trigger, frame, result);
                return;
            }

            _consecutiveSolveFailures = 0;
            PlateSolveSolution solution = result.Solution!;
            LearnFocalLength(solution);

            // The fit needs the physical pointing direction, so the catalogue
            // position goes back through the full apparent-place transform with
            // refraction included (D15).
            var observer = new ObserverSite(_site!.LatitudeDegrees, _site.LongitudeDegrees, _site.HeightMeters);
            HorizontalCoordinates direction = TopocentricConverter.ToAltAz(
                solution.CenterRaDegrees, solution.CenterDecDegrees, frame.ExposureMidpointUtc,
                observer, _options.EffectiveAtmosphere);
            var solved = new SolvedPosition(solution.CenterRaDegrees, solution.CenterDecDegrees, direction);

            if (_mode == SequenceMode.Unconnected)
            {
                OnUnconnectedSolve(trigger, frame, solved);
            }
            else
            {
                await OnConnectedSolveAsync(trigger, frame, solved).ConfigureAwait(false);
            }
        }
        finally
        {
            // Released after any failure has been kept, never before: a released
            // frame that is no longer on screen is deleted at once.
            hold.Dispose();
        }
    }

    private void OnSolveFailed(SampleTrigger trigger, CapturedImage frame, PlateSolveResult result)
    {
        _consecutiveSolveFailures++;
        string? kept = _frameStore.KeepFailed(frame.FitsPath, DateTime.UtcNow);

        _events.Publish(new SolveFailedEvent(
            trigger,
            $"Could not solve ({result.FailureReason}): {result.Message}",
            _consecutiveSolveFailures,
            kept));

        if (_mode == SequenceMode.Unconnected)
        {
            // A failure breaks the chain of agreeing solves: the mount may be
            // mid-turn, which is the commonest reason a frame fails at all.
            _lastUnconnectedSolve = null;
            ScheduleSolve(SampleTrigger.Periodic, _options.EffectiveSolveInterval);
            return;
        }

        // A forced sample is the user's one attempt; they can press again.
        // Otherwise a failed post-slew solve is tried again for as long as the
        // mount stays where it is -- a passing cloud is the usual reason.
        if (trigger != SampleTrigger.Forced && !_mountMoving)
        {
            ScheduleSolve(SampleTrigger.Retry, _options.EffectiveSolveInterval);
        }
    }

    private void OnUnconnectedSolve(SampleTrigger trigger, CapturedImage frame, SolvedPosition solved)
    {
        SolvedPosition? previous = _lastUnconnectedSolve;
        _lastUnconnectedSolve = solved;

        try
        {
            bool settled = trigger == SampleTrigger.Forced || (previous is not null && Agrees(previous, solved));
            if (!settled)
            {
                _events.Publish(new SampleSkippedEvent(
                    "Solved. Waiting for a second solve in the same place, to be sure the telescope has stopped " +
                    "moving (D26)."));
                return;
            }

            double rotation = TargetSelection.MechanicalRotationOf(
                _site!, solved.RaDegrees, solved.DecDegrees, frame.ExposureMidpointUtc);

            if (trigger != SampleTrigger.Forced && IsTooClose(rotation, out double nearest))
            {
                // Said only once the user has had time to move on; a mount
                // standing still after a sample would otherwise repeat this every
                // five seconds while they are simply reading the instruction.
                if (nearest >= 0.5)
                {
                    _events.Publish(new SampleSkippedEvent(
                        $"The telescope has turned {nearest:F1}° from the nearest sample; samples need to be at " +
                        $"least {_spacingDegrees:F0}° apart to widen the sweep (D27). Turn further, or record a " +
                        "sample to use this position anyway."));
                }

                return;
            }

            // D8 without a pier side to read. Turning past the meridian by hand
            // is not a flip, so it is worth a warning rather than an abort.
            if (_samples.Count > 0 && !_meridianWarned &&
                Math.Sign(rotation) != Math.Sign(_samples[0].RotationDegrees))
            {
                _meridianWarned = true;
                _events.Publish(new ManualActionRequiredEvent(
                    "The telescope is now on the other side of the meridian from the first sample. That is not a " +
                    "flip, so the measurement still holds, but the sequence was planned to stay on one side (D8).",
                    rotation));
            }

            AcceptSample(trigger, frame, solved, rotation);
        }
        finally
        {
            if (_sessionActive)
            {
                ScheduleSolve(SampleTrigger.Periodic, _options.EffectiveSolveInterval);
            }
        }
    }

    private async Task OnConnectedSolveAsync(SampleTrigger trigger, CapturedImage frame, SolvedPosition solved)
    {
        MountPosition position = await _mount!.GetPositionAsync(_sessionCts.Token).ConfigureAwait(false);

        if (!await PassesSafetyChecksAsync(position, _sessionCts.Token).ConfigureAwait(false))
        {
            return;
        }

        double rotation = TargetSelection.MechanicalRotationOf(
            _site!, position.RaDegrees, position.DecDegrees, DateTime.UtcNow);

        if (trigger != SampleTrigger.Forced && !_atConfirmedPosition && IsTooClose(rotation, out double nearest))
        {
            _events.Publish(new SampleSkippedEvent(
                $"The mount is {nearest:F1}° from the nearest sample; samples need to be at least " +
                $"{_spacingDegrees:F0}° apart (D27). Record a sample to use this position anyway."));
            return;
        }

        if (_samples.Count == 0)
        {
            // What the mount actually holds becomes the sequence's declination.
            // Every later slew is resolved to it (D16), and every later poll is
            // compared against it (D18).
            double declination = MechanicalDeclinationOf(_site!, position.RaDegrees, position.DecDegrees, DateTime.UtcNow);
            _plan = _plan! with { MechanicalDeclinationDegrees = declination };
        }

        AcceptSample(trigger, frame, solved, rotation);
    }

    /// <summary>
    /// Two solves in a row that agree mean the telescope has stopped (D26). Either
    /// frame will do: tracking holds the sky coordinates, and an undriven mount
    /// holds the horizon ones while the sky drifts past.
    /// </summary>
    private static bool Agrees(SolvedPosition previous, SolvedPosition current)
    {
        double sky = SeparationDegrees(previous.RaDegrees, previous.DecDegrees, current.RaDegrees, current.DecDegrees);
        double horizon = SeparationDegrees(
            previous.Direction.AzimuthDegrees, previous.Direction.AltitudeDegrees,
            current.Direction.AzimuthDegrees, current.Direction.AltitudeDegrees);
        return Math.Min(sky, horizon) * 60.0 <= StillToleranceArcminutes;
    }

    private void AcceptSample(SampleTrigger trigger, CapturedImage frame, SolvedPosition solved, double rotation)
    {
        _atConfirmedPosition = false;
        _observations.Add(solved.Direction);
        _samples.Add(new Sample(rotation, solved.RaDegrees, solved.DecDegrees, frame.ExposureMidpointUtc));

        _events.Publish(new PointCapturedEvent(
            new CapturePoint(_samples.Count, solved.RaDegrees, solved.DecDegrees, frame.ExposureMidpointUtc),
            trigger));

        if (_observations.Count >= SmallCircleFitter.MinimumObservations)
        {
            PublishEstimate();
        }

        ProposeNext();
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

        if (_samples.Count == 0)
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

    // ---- Geometry ----

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

    /// <summary>Great-circle angle between two points given as longitude and latitude, in degrees.</summary>
    private static double SeparationDegrees(double longitude1, double latitude1, double longitude2, double latitude2)
    {
        const double toRadians = Math.PI / 180.0;
        double dLat = (latitude2 - latitude1) * toRadians;
        double dLon = (longitude2 - longitude1) * toRadians;
        double a = Math.Pow(Math.Sin(dLat / 2), 2) +
                   Math.Cos(latitude1 * toRadians) * Math.Cos(latitude2 * toRadians) * Math.Pow(Math.Sin(dLon / 2), 2);
        return 2.0 * Math.Asin(Math.Min(1.0, Math.Sqrt(a))) / toRadians;
    }

    private static double WrapDegrees(double degrees)
    {
        double wrapped = degrees % 360.0;
        if (wrapped > 180.0)
        {
            wrapped -= 360.0;
        }
        else if (wrapped < -180.0)
        {
            wrapped += 360.0;
        }

        return wrapped;
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

    // ---- Resetting ----

    /// <summary>
    /// Discards the samples but keeps the sequence running, for a restart from a
    /// new position. A solve or slew still in flight belongs to the old
    /// generation and is dropped when it returns.
    /// </summary>
    private void ClearSamples()
    {
        _generation++;
        _atConfirmedPosition = false;
        CancelPendingSolve();
        _armed = null;
        _samples.Clear();
        _observations.Clear();
        _lastUnconnectedSolve = null;
        _consecutiveSolveFailures = 0;
        _initialPierSide = PierSide.Unknown;
        _initialRotationSign = 0;
        _meridianWarned = false;
        _proposal = null;
    }

    private void ResetSession()
    {
        ClearSamples();
        _sessionActive = false;
        _plan = null;

        // Cancels a slew or solve still running for the sequence being ended.
        // The capture loop is not the sequence's, and carries on.
        _sessionCts.Cancel();
        _sessionCts.Dispose();
        _sessionCts = new CancellationTokenSource();
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        CancellationTokenSource? loop = _loopCts;
        Task? loopTask = _loopTask;
        _loopCts = null;
        _loopTask = null;
        loop?.Cancel();
        _sessionCts.Cancel();
        _pendingDelay?.Cancel();

        try
        {
            // Bounded, because a driver stuck in an exposure is not a reason for
            // the application to hang on the way out.
            loopTask?.Wait(TimeSpan.FromSeconds(5));
        }
        catch (Exception)
        {
        }

        _frameStore.Dispose();

        if (_ownsCamera)
        {
            _camera?.Dispose();
        }

        if (_ownsMount)
        {
            _mount?.Dispose();
        }

        _events.Dispose();
    }
}
