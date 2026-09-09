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
public sealed record SimulatedMountOptions(
    GeodeticLocation Site,
    MountMisalignment Misalignment,
    double ConeErrorArcminutes = 0.0,
    double ConePhaseDegrees = 0.0,
    bool Tracking = true,
    TimeSpan SlewDuration = default,
    bool SupportsSideOfPier = true,
    bool CanSlewAsync = true);

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

    public SimulatedMount(SimulatedMountOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        Options = options;
        _misalignment = options.Misalignment;
        _observerSite = new ObserverSite(options.Site.LatitudeDegrees, options.Site.LongitudeDegrees, options.Site.HeightMeters);
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
    /// </summary>
    public Task<MountPosition> GetPositionAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        RequireConnected();

        lock (_gate)
        {
            return Task.FromResult(new MountPosition(
                _commandedRaDegrees, _commandedDecDegrees, ComputeSideOfPier(DateTime.UtcNow), DateTime.UtcNow));
        }
    }

    public async Task SlewToCoordinatesAsync(double raDegrees, double decDegrees, CancellationToken cancellationToken = default)
    {
        RequireConnected();
        if (!CanSlewAsync)
        {
            throw new NotSupportedException("This simulated mount is configured without slew support (D9); use manual mode.");
        }

        if (Options.SlewDuration > TimeSpan.Zero)
        {
            await Task.Delay(Options.SlewDuration, cancellationToken).ConfigureAwait(false);
        }

        cancellationToken.ThrowIfCancellationRequested();
        RequireConnected();

        lock (_gate)
        {
            _commandedRaDegrees = raDegrees;
            _commandedDecDegrees = decDegrees;
            _mechanicalEpochUtc = DateTime.UtcNow;
            _hasSlewed = true;
        }
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
    public HorizontalCoordinates PhysicalPointingAt(DateTime utc)
    {
        double commandedRa, commandedDec;
        MountMisalignment misalignment;
        DateTime mechanicalEpoch;
        bool hasSlewed;

        lock (_gate)
        {
            commandedRa = _commandedRaDegrees;
            commandedDec = _commandedDecDegrees;
            misalignment = _misalignment;
            mechanicalEpoch = _mechanicalEpochUtc;
            hasSlewed = _hasSlewed;
        }

        if (!hasSlewed)
        {
            throw new InvalidOperationException("The simulated mount has not been slewed anywhere yet.");
        }

        // With the drive stopped the mechanical angles stay where the slew left
        // them, so they are evaluated at the slew's epoch; while tracking they
        // are evaluated now, which advances them at the sidereal rate.
        DateTime mechanicalTime = Options.Tracking ? utc : mechanicalEpoch;

        // What an ideally aligned mount would have to do to obey the command.
        // Refraction is deliberately excluded: a mount's pointing model works in
        // geometric coordinates, and pretending otherwise would hand the
        // simulator knowledge real hardware does not have.
        HorizontalCoordinates ideal = TopocentricConverter.ToAltAz(
            commandedRa, commandedDec, mechanicalTime, _observerSite, AtmosphericConditions.Vacuum);

        var (rotationDegrees, declinationDegrees) = MountMechanics.Decompose(
            Options.Site.LatitudeDegrees, ideal);

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

            HorizontalCoordinates ideal = TopocentricConverter.ToAltAz(
                _commandedRaDegrees, _commandedDecDegrees, utc, _observerSite, AtmosphericConditions.Vacuum);

            var (rotationDegrees, _) = MountMechanics.Decompose(Options.Site.LatitudeDegrees, ideal);
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
}
