using FreePolarAlign.Core.Alignment;
using FreePolarAlign.Core.Astrometry;

namespace FreePolarAlign.Devices.Simulated;

/// <summary>
/// The state of the simulated sky and mount: what is actually wrong with the
/// telescope, which the software must discover without being told.
/// </summary>
/// <param name="Misalignment">
/// The injected polar axis error. This is the ground truth every accuracy claim
/// in the roadmap is measured against.
/// </param>
/// <param name="ConeErrorArcminutes">
/// Angle between the optical axis and where it would sit on a perfectly squared
/// instrument. Should make no difference to the recovered axis at all -- the
/// point of injecting it is to prove that.
/// </param>
/// <param name="Tracking">
/// Whether the mount drives at sidereal rate. Tracking is a rotation about the
/// same axis the measurement is about, so it changes nothing the fit cares
/// about; the option exists to demonstrate that rather than to be tuned.
/// </param>
/// <param name="InitialMechanicalRotationDegrees">
/// Where the mount is already pointing when it is switched on, as an hour angle
/// in its own frame. A real telescope is always aimed somewhere, and since the
/// first capture of a sequence is now taken without moving anything (D18), a
/// simulator that begins nowhere at all could not exercise that path.
/// </param>
/// <param name="InitialPoleDistanceDegrees">
/// The initial pointing's distance from the celestial pole, on the observer's
/// own side of the equator. Expressed as a pole distance rather than a
/// declination so that the same default is sensible in either hemisphere.
///
/// The default is chosen to sit inside the declination band the committed
/// sample catalogue actually covers (D13), and comfortably clear of the twenty
/// degrees inside which the arc stops curving measurably (D11). A simulated
/// telescope pointed at empty catalogue would fail its first solve with "no
/// stars detected", which is a confusing way to discover a test-data
/// limitation.
/// </param>
public sealed record SimulatedMountOptions(
    GeodeticLocation Site,
    MountMisalignment Misalignment,
    double ConeErrorArcminutes = 0.0,
    double ConePhaseDegrees = 0.0,
    bool Tracking = true,
    TimeSpan SlewDuration = default,
    bool SupportsSideOfPier = true,
    bool CanSlewAsync = true,
    double InitialMechanicalRotationDegrees = 12.0,
    double InitialPoleDistanceDegrees = 22.0);

/// <summary>
/// A German equatorial mount with a genuinely misaligned polar axis.
///
/// The mechanical model is the point of this class, and it is what makes the
/// virtual observatory a real test rather than a mock. When commanded to a
/// position, the mount works out the mechanical angles an *ideally aligned*
/// instrument would need -- because that is all a real mount can do -- and then
/// drives to those angles about the axis it *actually* has. The telescope
/// therefore points somewhere other than where it was told, by exactly the
/// amount and in exactly the direction the injected misalignment implies, and
/// nothing anywhere in the software is told what that error is.
///
/// Tracking follows from the same construction rather than being modelled
/// separately: recomputing the mechanical angles at a later time advances them
/// at the sidereal rate on their own.
///
/// A slew takes <see cref="SimulatedMountOptions.SlewDuration"/>, and during it
/// the mount is genuinely somewhere in between: its mechanical angles move
/// linearly from where it was to where it is going, and both what it reports
/// and what the camera sees follow them. A frame exposed mid-slew therefore
/// lands between the two fields, as on real hardware, which is what the engine
/// must learn not to use (D26). The angles are interpolated rather than the sky
/// coordinates because that is what the motors do, and because interpolating
/// J2000 coordinates would move the mechanical declination on the way (D16).
///
/// There is no separate entry point for motion the engine did not ask for. A
/// test calling <see cref="SlewToCoordinatesAsync"/> itself is, as far as the
/// engine can tell, exactly a hand-controller slew or another program's, and
/// <see cref="NudgeDeclination"/> is a press of a declination button.
/// </summary>
public sealed class SimulatedMount : IMount
{
    private readonly object _gate = new();
    private readonly ObserverSite _observerSite;

    private MountMisalignment _misalignment;
    private double _commandedRaDegrees;
    private double _commandedDecDegrees;
    private DateTime _mechanicalEpochUtc;
    private bool _hasSlewed;
    private Slew? _slew;
    private int _slewGeneration;

    public SimulatedMount(SimulatedMountOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        Options = options;
        _misalignment = options.Misalignment;
        _observerSite = new ObserverSite(options.Site.LatitudeDegrees, options.Site.LongitudeDegrees, options.Site.HeightMeters);

        // Switched on already pointing somewhere, like the real thing. The
        // commanded coordinates are derived from the configured mechanical
        // angles exactly as a slew would derive them, so the mount's belief and
        // its mechanical state agree from the first moment rather than only
        // after the first slew.
        // Mechanical declination is measured towards the pole the mount's axis
        // actually points at -- the visible one -- so it is positive in both
        // hemispheres and is not sky declination south of the equator.
        double declination = 90.0 - options.InitialPoleDistanceDegrees;

        HorizontalCoordinates ideal = MountMechanics.Compose(
            options.Site.LatitudeDegrees,
            MountMisalignment.Aligned,
            options.InitialMechanicalRotationDegrees,
            declination);

        _mechanicalEpochUtc = DateTime.UtcNow;
        (_commandedRaDegrees, _commandedDecDegrees) = TopocentricConverter.FromAltAz(
            ideal, _mechanicalEpochUtc, _observerSite, AtmosphericConditions.Vacuum);
        _hasSlewed = true;
    }

    public SimulatedMountOptions Options { get; }

    public string Name => "Simulated Mount";

    public bool IsConnected { get; private set; }

    public bool CanSlewAsync => Options.CanSlewAsync;

    /// <summary>The misalignment currently injected. Ground truth, for tests only.</summary>
    public MountMisalignment CurrentMisalignment
    {
        get
        {
            lock (_gate)
            {
                return _misalignment;
            }
        }
    }

    /// <summary>
    /// Where the polar axis actually points. Ground truth, for tests only -- no
    /// production code path may consult this, since discovering it is the entire
    /// job of the software.
    /// </summary>
    public HorizontalCoordinates TruePolarAxis =>
        MountForwardModel.MountAxis(Options.Site.LatitudeDegrees, CurrentMisalignment);

    /// <summary>
    /// Turns the altitude and azimuth bolts, by the given amounts. The
    /// convergence loop uses this to act on what the software reports, which is
    /// what makes the exit criterion a closed loop rather than a single
    /// measurement.
    /// </summary>
    public void AdjustAxis(double altitudeArcminutes, double azimuthArcminutes)
    {
        lock (_gate)
        {
            _misalignment = new MountMisalignment(
                _misalignment.AltitudeErrorArcminutes + altitudeArcminutes,
                _misalignment.AzimuthErrorArcminutes + azimuthArcminutes);
        }
    }

    /// <summary>
    /// Moves the mechanical declination by the given amount, as holding a
    /// declination button on a hand controller would. The mount knows it moved,
    /// so the coordinates it reports follow -- which is what lets the engine
    /// notice that a sequence's fixed declination (D11) has been broken.
    /// </summary>
    public void NudgeDeclination(double arcminutes)
    {
        double degrees = arcminutes / 60.0;

        lock (_gate)
        {
            DateTime now = DateTime.UtcNow;

            if (_slew is { } slew && now < slew.EndUtc)
            {
                // The whole path moves, so the slew still ends where the press
                // has put it rather than quietly undoing the press on arrival.
                (double ra, double dec) = BeliefFromMechanics(
                    slew.EndRotationDegrees, slew.EndDeclinationDegrees + degrees, slew.EndUtc);
                _slew = slew with
                {
                    StartDeclinationDegrees = slew.StartDeclinationDegrees + degrees,
                    EndDeclinationDegrees = slew.EndDeclinationDegrees + degrees,
                    TargetRaDegrees = ra,
                    TargetDecDegrees = dec,
                };
                return;
            }

            SettleIfArrived(now);
            (double rotation, double declination) = MechanicsAt(now);
            (_commandedRaDegrees, _commandedDecDegrees) = BeliefFromMechanics(rotation, declination + degrees, now);
            _mechanicalEpochUtc = now;
        }
    }

    /// <summary>Simulates the mount dropping off the bus mid-sequence.</summary>
    public void SimulateDisconnection() => IsConnected = false;

    public Task ConnectAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        IsConnected = true;
        return Task.CompletedTask;
    }

    public Task DisconnectAsync(CancellationToken cancellationToken = default)
    {
        IsConnected = false;
        return Task.CompletedTask;
    }

    /// <summary>
    /// What the mount believes, which is the position it was commanded to --
    /// not where it is actually pointing. A misaligned mount has no way to know
    /// the difference, and software that trusted this instead of plate solving
    /// would inherit the very error it is trying to measure.
    ///
    /// Mid-slew it is wherever the encoders say the mount has got to, read
    /// through the same ideal model, so the reported position changes during a
    /// slew as a real mount's does.
    /// </summary>
    public Task<MountPosition> GetPositionAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        RequireConnected();

        lock (_gate)
        {
            DateTime now = DateTime.UtcNow;
            SettleIfArrived(now);

            double ra = _commandedRaDegrees;
            double dec = _commandedDecDegrees;
            bool slewing = _slew is not null;
            if (slewing)
            {
                (double rotation, double declination) = MechanicsAt(now);
                (ra, dec) = BeliefFromMechanics(rotation, declination, now);
            }

            return Task.FromResult(new MountPosition(
                ra,
                dec,
                ComputeSideOfPier(now),
                now,
                Options.Tracking ? TrackingState.Tracking : TrackingState.Stopped,
                slewing));
        }
    }

    /// <summary>
    /// Slews over <see cref="SimulatedMountOptions.SlewDuration"/>. A slew
    /// started while another is running replaces it from wherever the mount
    /// has got to, as a hand controller's does, and the replaced call then
    /// waits for the mount to stop -- what polling <c>Slewing</c> on a real
    /// driver would do. Cancelling stops the mount where it is, but only while
    /// the slew is still this call's own: one that has been replaced is someone
    /// else's motion.
    /// </summary>
    public async Task SlewToCoordinatesAsync(double raDegrees, double decDegrees, CancellationToken cancellationToken = default)
    {
        RequireConnected();
        if (!CanSlewAsync)
        {
            throw new NotSupportedException("This simulated mount is configured without slew support (D9); use manual mode.");
        }

        cancellationToken.ThrowIfCancellationRequested();

        if (Options.SlewDuration <= TimeSpan.Zero)
        {
            lock (_gate)
            {
                _slew = null;
                _commandedRaDegrees = raDegrees;
                _commandedDecDegrees = decDegrees;
                _mechanicalEpochUtc = DateTime.UtcNow;
                _hasSlewed = true;
            }

            return;
        }

        int generation;
        lock (_gate)
        {
            DateTime now = DateTime.UtcNow;
            DateTime end = now + Options.SlewDuration;
            (double startRotation, double startDeclination) = MechanicsAt(now);

            // The target is resolved exactly as a settled mount resolves its
            // commanded coordinates (D16), at the instant the slew arrives, so
            // the interpolation ends precisely where the settled model takes
            // over and nothing jumps at the boundary.
            (double endRotation, double endDeclination) = Decompose(raDegrees, decDegrees, end);

            generation = ++_slewGeneration;
            _slew = new Slew(
                generation, now, end,
                startRotation, startDeclination,
                endRotation, endDeclination,
                raDegrees, decDegrees);
            _hasSlewed = true;
        }

        try
        {
            while (true)
            {
                TimeSpan remaining;
                lock (_gate)
                {
                    DateTime now = DateTime.UtcNow;
                    SettleIfArrived(now);
                    if (_slew is null)
                    {
                        break;
                    }

                    remaining = _slew.EndUtc - now;
                }

                await Task.Delay(remaining, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            lock (_gate)
            {
                if (_slew?.Generation == generation)
                {
                    StopWhereItIs(DateTime.UtcNow);
                }
            }

            throw;
        }

        cancellationToken.ThrowIfCancellationRequested();
        RequireConnected();
    }

    public Task<PierSide> GetSideOfPierAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        RequireConnected();

        return Task.FromResult(Options.SupportsSideOfPier ? ComputeSideOfPier(DateTime.UtcNow) : PierSide.Unknown);
    }

    public Task<GeodeticLocation> GetSiteLocationAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(Options.Site);
    }

    /// <summary>
    /// Where the telescope is really aimed at a given instant, in the local
    /// horizon frame. This is the physical truth the camera sees and the
    /// software never learns directly.
    /// </summary>
    /// <remarks>
    /// A time during a slew gives the interpolated position, which is how a
    /// camera exposing mid-slew comes to render a field in between.
    /// </remarks>
    public HorizontalCoordinates PhysicalPointingAt(DateTime utc)
    {
        MountMisalignment misalignment;
        double rotationDegrees, declinationDegrees;

        lock (_gate)
        {
            if (!_hasSlewed)
            {
                throw new InvalidOperationException("The simulated mount has not been slewed anywhere yet.");
            }

            misalignment = _misalignment;
            (rotationDegrees, declinationDegrees) = MechanicsAt(utc);
        }

        // The same mechanical angles, driven about the axis the mount actually
        // has. This is where the injected misalignment enters, and it is the
        // only place it does.
        return MountMechanics.Compose(
            Options.Site.LatitudeDegrees,
            misalignment,
            rotationDegrees,
            declinationDegrees,
            Options.ConeErrorArcminutes,
            Options.ConePhaseDegrees);
    }

    /// <summary>
    /// The mount's mechanical angles at an instant: interpolated while a slew
    /// is under way, otherwise derived from the commanded coordinates as a
    /// settled mount derives them. Caller holds the gate.
    /// </summary>
    private (double RotationDegrees, double DeclinationDegrees) MechanicsAt(DateTime utc)
    {
        if (_slew is { } slew)
        {
            if (utc >= slew.EndUtc)
            {
                return Decompose(slew.TargetRaDegrees, slew.TargetDecDegrees, Options.Tracking ? utc : slew.EndUtc);
            }

            double fraction = Math.Clamp((utc - slew.StartUtc) / (slew.EndUtc - slew.StartUtc), 0.0, 1.0);

            // The short way round, so a slew across rotation ±180 does not
            // swing the long way through the other side of the pier.
            double rotationChange = slew.EndRotationDegrees - slew.StartRotationDegrees;
            rotationChange -= 360.0 * Math.Round(rotationChange / 360.0);

            return (
                slew.StartRotationDegrees + fraction * rotationChange,
                slew.StartDeclinationDegrees + fraction * (slew.EndDeclinationDegrees - slew.StartDeclinationDegrees));
        }

        // With the drive stopped the mechanical angles stay where the slew left
        // them, so they are evaluated at the slew's epoch; while tracking they
        // are evaluated now, which advances them at the sidereal rate.
        return Decompose(_commandedRaDegrees, _commandedDecDegrees, Options.Tracking ? utc : _mechanicalEpochUtc);
    }

    /// <summary>
    /// What an ideally aligned mount would have to do to obey a command.
    /// Refraction is deliberately excluded: a mount's pointing model works in
    /// geometric coordinates, and pretending otherwise would hand the
    /// simulator knowledge real hardware does not have.
    /// </summary>
    private (double RotationDegrees, double DeclinationDegrees) Decompose(double raDegrees, double decDegrees, DateTime utc)
    {
        HorizontalCoordinates ideal = TopocentricConverter.ToAltAz(
            raDegrees, decDegrees, utc, _observerSite, AtmosphericConditions.Vacuum);

        return MountMechanics.Decompose(Options.Site.LatitudeDegrees, ideal);
    }

    /// <summary>
    /// The coordinates a mount at these mechanical angles believes it points
    /// at: the exact inverse of <see cref="Decompose"/>, so that a mount which
    /// stops somewhere of its own choosing agrees with itself afterwards.
    /// </summary>
    private (double RaDegrees, double DecDegrees) BeliefFromMechanics(double rotationDegrees, double declinationDegrees, DateTime utc)
    {
        HorizontalCoordinates ideal = MountMechanics.Compose(
            Options.Site.LatitudeDegrees, MountMisalignment.Aligned, rotationDegrees, declinationDegrees);

        return TopocentricConverter.FromAltAz(ideal, utc, _observerSite, AtmosphericConditions.Vacuum);
    }

    /// <summary>
    /// Folds a slew that has arrived into the settled state, with the arrival
    /// as the mechanical epoch -- not whenever the waiting call happened to
    /// wake, which would leave an untracked mount's angles off by the delay.
    /// Caller holds the gate.
    /// </summary>
    private void SettleIfArrived(DateTime utc)
    {
        if (_slew is { } slew && utc >= slew.EndUtc)
        {
            _commandedRaDegrees = slew.TargetRaDegrees;
            _commandedDecDegrees = slew.TargetDecDegrees;
            _mechanicalEpochUtc = slew.EndUtc;
            _slew = null;
        }
    }

    /// <summary>
    /// Ends the slew wherever it has got to, as an aborted slew does: the mount
    /// then believes it is where its encoders say, and tracks from there.
    /// Caller holds the gate.
    /// </summary>
    private void StopWhereItIs(DateTime utc)
    {
        SettleIfArrived(utc);
        if (_slew is null)
        {
            return;
        }

        (double rotation, double declination) = MechanicsAt(utc);
        (_commandedRaDegrees, _commandedDecDegrees) = BeliefFromMechanics(rotation, declination, utc);
        _mechanicalEpochUtc = utc;
        _slew = null;
    }

    /// <summary>
    /// Pier side from the commanded hour angle. Deliberately derived rather than
    /// tracked as state, so that a driver configured not to support it
    /// (<see cref="SimulatedMountOptions.SupportsSideOfPier"/>) can return
    /// Unknown and exercise D9's "advisory only" path.
    /// </summary>
    private PierSide ComputeSideOfPier(DateTime utc)
    {
        lock (_gate)
        {
            if (!_hasSlewed)
            {
                return PierSide.Unknown;
            }

            // Settled, the hour angle is taken at the given time even with the
            // drive stopped, as it always was; mid-slew it is wherever the
            // motors have got to.
            double rotationDegrees = _slew is { } slew
                ? utc < slew.EndUtc
                    ? MechanicsAt(utc).RotationDegrees
                    : Decompose(slew.TargetRaDegrees, slew.TargetDecDegrees, utc).RotationDegrees
                : Decompose(_commandedRaDegrees, _commandedDecDegrees, utc).RotationDegrees;

            return rotationDegrees >= 0.0 ? PierSide.West : PierSide.East;
        }
    }

    private void RequireConnected()
    {
        if (!IsConnected)
        {
            throw new InvalidOperationException("The simulated mount is not connected.");
        }
    }

    public void Dispose() => IsConnected = false;

    /// <summary>
    /// A slew under way. The start angles are held rather than tracked: a slew
    /// outruns the sidereal drive by orders of magnitude, and holding them
    /// keeps the motion exactly linear, which is what makes a mid-slew position
    /// checkable.
    /// </summary>
    private sealed record Slew(
        int Generation,
        DateTime StartUtc,
        DateTime EndUtc,
        double StartRotationDegrees,
        double StartDeclinationDegrees,
        double EndRotationDegrees,
        double EndDeclinationDegrees,
        double TargetRaDegrees,
        double TargetDecDegrees);
}
