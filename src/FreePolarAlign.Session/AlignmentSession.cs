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

/// <summary>Emitted once a target has been chosen, so the UI can explain itself.</summary>
public sealed record TargetSelectedEvent(
    double DeclinationDegrees,
    bool IsWestOfMeridian,
    int PlannedCaptures,
    double SweepDegrees) : EngineEvent;

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
/// The alignment sequence: choose a target, capture, solve, fit, and report --
/// with a way out at every step.
///
/// The engine is driven by commands and answers only with events (D6), so the
/// same object serves a local UI now and a remote one later without change. It
/// deliberately exposes no "current state" property: callers reconstruct
/// whatever they need from the event stream, which is what keeps the boundary
/// message-shaped rather than merely message-flavoured.
/// </summary>
public sealed class AlignmentSession : IAlignmentEngine, IDisposable
{
    private readonly ICamera _camera;
    private readonly IMount _mount;
    private readonly ISolver _solver;
    private readonly AlignmentSessionOptions _options;
    private readonly EventStream _events = new();
    private readonly SemaphoreSlim _commandGate = new(1, 1);

    /// <summary>
    /// How many failed solves in a row before the session gives up. Retrying is
    /// right for transient trouble; past a handful the cause is structural --
    /// the wrong index pack, a badly wrong focal length, thick cloud -- and
    /// silently retrying forever would leave the user watching nothing happen.
    /// </summary>
    private const int MaximumConsecutiveSolveFailures = 3;

    private readonly List<HorizontalCoordinates> _observations = new();
    private GeodeticLocation? _site;
    private TargetPlan? _plan;
    private PierSide _initialPierSide = PierSide.Unknown;
    private double _initialRotationSign;
    private int _completedCaptures;
    private int _consecutiveSolveFailures;
    private bool _sessionActive;
    private bool _awaitingManualAction;
    private EquipmentProfile? _profile;

    public AlignmentSession(ICamera camera, IMount mount, ISolver solver, AlignmentSessionOptions? options = null)
    {
        _camera = camera ?? throw new ArgumentNullException(nameof(camera));
        _mount = mount ?? throw new ArgumentNullException(nameof(mount));
        _solver = solver ?? throw new ArgumentNullException(nameof(solver));
        _options = options ?? new AlignmentSessionOptions();
        _profile = _options.EquipmentProfile;
    }

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
                case StartSessionCommand start:
                    await StartAsync(start, cancellationToken).ConfigureAwait(false);
                    break;
                case CaptureNextPointCommand:
                    await CaptureNextAsync(cancellationToken).ConfigureAwait(false);
                    break;
                case CancelSessionCommand:
                    Reset();
                    _events.Publish(new SessionCompletedEvent());
                    break;
                case AbortSessionCommand abort:
                    Reset();
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

    private async Task StartAsync(StartSessionCommand start, CancellationToken cancellationToken)
    {
        Reset();

        try
        {
            if (!_camera.IsConnected)
            {
                await _camera.ConnectAsync(cancellationToken).ConfigureAwait(false);
            }

            if (!_mount.IsConnected)
            {
                await _mount.ConnectAsync(cancellationToken).ConfigureAwait(false);
            }

            // The mount's own site properties are the preferred source (D14),
            // falling back to whatever the caller configured.
            GeodeticLocation site;
            try
            {
                site = await _mount.GetSiteLocationAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                site = new GeodeticLocation(
                    start.Configuration.SiteLatitudeDegrees,
                    start.Configuration.SiteLongitudeDegrees,
                    start.Configuration.SiteHeightMeters);
            }

            _site = site;

            if (!_options.ManualMode && !_mount.CanSlewAsync)
            {
                _events.Publish(new SessionFaultedEvent(
                    "This mount cannot perform absolute slews (D9), so automatic mode is unavailable. Use manual mode."));
                return;
            }

            TargetPlan plan = TargetSelection.Plan(
                site, DateTime.UtcNow, _options.CaptureCount, _options.SweepDegrees, _options.EffectiveAtmosphere);

            if (!plan.Success)
            {
                _events.Publish(new SessionFaultedEvent(plan.Reason ?? "No usable target could be selected."));
                return;
            }

            _plan = plan;
            _sessionActive = true;

            _events.Publish(new SessionStartedEvent(start.Configuration));
            _events.Publish(new TargetSelectedEvent(
                plan.MechanicalDeclinationDegrees, plan.IsWest, plan.Captures.Count, _options.SweepDegrees));
        }
        catch (OperationCanceledException)
        {
            Reset();
            throw;
        }
        catch (Exception ex)
        {
            Reset();
            _events.Publish(new SessionFaultedEvent($"Could not start the session: {ex.Message}"));
        }
    }

    private async Task CaptureNextAsync(CancellationToken cancellationToken)
    {
        if (!_sessionActive || _plan is null || _site is null)
        {
            _events.Publish(new SessionFaultedEvent("No session is running."));
            return;
        }

        if (_completedCaptures >= _plan.Captures.Count)
        {
            Reset();
            _events.Publish(new SessionCompletedEvent());
            return;
        }

        PlannedCapture planned = _plan.Captures[_completedCaptures];

        try
        {
            // Resolved now, not at planning time: the mechanical declination has
            // to stay constant, and coordinates computed for a stale instant
            // would make the mount drift in declination (see
            // TargetSelection.ResolveCommand).
            (double commandRa, double commandDec) = TargetSelection.ResolveCommand(
                _site, planned.MechanicalRotationDegrees, _plan.MechanicalDeclinationDegrees, DateTime.UtcNow);

            if (_options.ManualMode)
            {
                if (!_awaitingManualAction)
                {
                    _awaitingManualAction = true;
                    _events.Publish(new ManualActionRequiredEvent(
                        BuildManualInstruction(planned), planned.MechanicalRotationDegrees));
                    return;
                }

                _awaitingManualAction = false;
            }
            else
            {
                await _mount.SlewToCoordinatesAsync(commandRa, commandDec, cancellationToken)
                    .ConfigureAwait(false);
            }

            if (!await PassesSafetyChecksAsync(commandRa, commandDec, cancellationToken).ConfigureAwait(false))
            {
                return;
            }

            CapturedImage captured = await _camera
                .ExposeAsync(_options.EffectiveExposure, cancellationToken).ConfigureAwait(false);

            PlateSolveResult solve = await SolveAsync(captured, commandRa, commandDec, cancellationToken).ConfigureAwait(false);
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
                    Reset();
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
            var observer = new ObserverSite(_site.LatitudeDegrees, _site.LongitudeDegrees, _site.HeightMeters);
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

            if (_completedCaptures >= _plan.Captures.Count)
            {
                _sessionActive = false;
                _events.Publish(new SessionCompletedEvent());
            }
        }
        catch (OperationCanceledException)
        {
            Reset();
            throw;
        }
        catch (Exception ex)
        {
            // Anything the hardware throws mid-sequence -- a pulled cable, a
            // driver dying -- has to leave a recoverable session rather than an
            // unhandled exception, so the state is torn down and reported.
            Reset();
            _events.Publish(new SessionFaultedEvent($"Capture {_completedCaptures + 1} failed: {ex.Message}"));
        }
    }

    /// <summary>
    /// D8's meridian check, made twice over on purpose. The driver's pier side
    /// is consulted when it is available, and the hour angle is computed
    /// independently from the commanded coordinates and the clock -- because
    /// some drivers report pier side unreliably (D9), and a flip that goes
    /// unnoticed still returns a confident, meaningless answer.
    /// </summary>
    private async Task<bool> PassesSafetyChecksAsync(double commandRa, double commandDec, CancellationToken cancellationToken)
    {
        double rotation = TargetSelection.MechanicalRotationOf(_site!, commandRa, commandDec, DateTime.UtcNow);
        double sign = Math.Sign(rotation);

        PierSide pierSide = PierSide.Unknown;
        try
        {
            pierSide = await _mount.GetSideOfPierAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Advisory only (D9): a driver that cannot answer must not stop a
            // sequence, because the computed hour angle carries the check.
        }

        if (_completedCaptures == 0)
        {
            _initialPierSide = pierSide;
            _initialRotationSign = sign;
            return true;
        }

        if (sign != 0 && _initialRotationSign != 0 && sign != _initialRotationSign)
        {
            Reset();
            _events.Publish(new AlignmentWithheldEvent(
                "The sequence has crossed the meridian, which inverts the sense of cone error and makes the fit " +
                "meaningless (D8). Restart the sequence on one side."));
            return false;
        }

        if (pierSide != PierSide.Unknown && _initialPierSide != PierSide.Unknown && pierSide != _initialPierSide)
        {
            Reset();
            _events.Publish(new AlignmentWithheldEvent(
                $"The mount reports it flipped from {_initialPierSide} to {pierSide} mid-sequence (D8). " +
                "Restart the sequence on one side of the meridian."));
            return false;
        }

        return true;
    }

    private async Task<PlateSolveResult> SolveAsync(CapturedImage captured, double commandRa, double commandDec, CancellationToken cancellationToken)
    {
        // A position hint costs nothing here -- the mount was just commanded
        // somewhere -- and turns a blind search into a bounded one. The scale
        // hint is only offered once the focal length has actually been measured,
        // since a claimed one is routinely several percent out.
        double? scaleHint = _profile is { IsFocalLengthSolved: true } profile
            ? profile.ExpectedScaleArcsecondsPerPixel
            : null;

        var request = new PlateSolveRequest(
            captured.FitsPath,
            ApproximateScaleArcsecPerPixel: scaleHint,
            ScaleToleranceFraction: scaleHint is null ? null : _profile!.ScaleToleranceFraction,
            ApproximateRaDegrees: commandRa,
            ApproximateDecDegrees: commandDec,
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
                _camera.Name, _camera.PixelSizeMicrons, _camera.SensorWidthPixels, _camera.SensorHeightPixels);
        }

        _profile = _profile.WithSolvedScale(solution.PixelScaleArcsecPerPixel);
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

    private static string BuildManualInstruction(PlannedCapture planned)
    {
        string direction = planned.MechanicalRotationDegrees >= 0 ? "west" : "east";
        return $"Rotate the mount in RA to about {Math.Abs(planned.MechanicalRotationDegrees):F0}° {direction} " +
               "of the meridian. Do not touch declination. Then continue.";
    }

    private void Reset()
    {
        _sessionActive = false;
        _awaitingManualAction = false;
        _completedCaptures = 0;
        _consecutiveSolveFailures = 0;
        _initialPierSide = PierSide.Unknown;
        _initialRotationSign = 0;
        _observations.Clear();
        _plan = null;
    }

    public void Dispose()
    {
        _commandGate.Dispose();
        _events.Dispose();
    }
}
