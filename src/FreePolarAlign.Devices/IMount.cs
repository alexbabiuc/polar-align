namespace FreePolarAlign.Devices;

/// <summary>
/// Which side of the pier the mount currently reports, or <see cref="Unknown"/>
/// if the driver does not support the query. D9 treats this as advisory only,
/// since some drivers report it unreliably; callers must independently compute
/// hour angle as a cross-check (D8) rather than trusting this alone.
/// </summary>
public enum PierSide
{
    Unknown,
    East,
    West
}

/// <summary>
/// Whether the mount is driving at sidereal rate, with <see cref="Unknown"/> for
/// drivers that do not say. Unknown is reported as unknown rather than folded
/// into "stopped": the two look identical in a boolean and mean very different
/// things to someone deciding whether the readings they are watching should be
/// changing on their own.
/// </summary>
public enum TrackingState
{
    Unknown,
    Tracking,
    Stopped
}

/// <summary>Geodetic observer location. Latitude accuracy is a hard requirement (D14).</summary>
public sealed record GeodeticLocation(double LatitudeDegrees, double LongitudeDegrees, double HeightMeters);

/// <summary>
/// A mount's reported state at a moment in time: where it believes it is
/// pointing, which side of the pier it is on, and whether it is tracking.
///
/// All three arrive together because they are read together -- one round trip to
/// a driver rather than three, which matters for a COM-based ASCOM mount where
/// each property read is an out-of-process call.
/// </summary>
/// <param name="IsSlewing">
/// The driver says the mount is moving under its own power. It exists because
/// a sample is solved when a slew ends (D26), and a frame exposed while the
/// mount was moving is a smear of the positions it passed through.
///
/// Advisory, like <see cref="PierSide"/>. ASCOM defines <c>Slewing</c> in terms
/// of the slews, parks and axis moves a client asked for, and whether a driver
/// also reports motion started from a hand controller or another program is
/// unknown (see docs/DEVICE-COMPATIBILITY.md). The engine therefore also treats
/// a reported position that is still changing as motion, and false here means
/// only that the driver did not say otherwise.
/// </param>
public sealed record MountPosition(
    double RaDegrees,
    double DecDegrees,
    PierSide PierSide,
    DateTime TimestampUtc,
    TrackingState Tracking = TrackingState.Unknown,
    bool IsSlewing = false);

/// <summary>
/// A mount device, opened via <see cref="IDeviceProvider.OpenMount"/>. Slewing
/// is exposed only as an absolute coordinate slew (D9: "RA steps are commanded
/// as absolute slews... rather than timed axis motion"); there is deliberately
/// no <c>MoveAxis</c>-style timed motion in this contract, because that
/// accumulates rate and latency error that would need per-mount calibration.
/// </summary>
public interface IMount : IDisposable
{
    string Name { get; }

    bool IsConnected { get; }

    /// <summary>
    /// Whether this mount supports <see cref="SlewToCoordinatesAsync"/>. A
    /// mount without it is not usable in automatic mode (D9); manual mode
    /// (D10) is the fallback and does not go through this interface at all.
    /// </summary>
    bool CanSlewAsync { get; }

    Task ConnectAsync(CancellationToken cancellationToken = default);

    Task DisconnectAsync(CancellationToken cancellationToken = default);

    Task<MountPosition> GetPositionAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Slews to absolute J2000 coordinates and waits for the slew to complete.
    /// Closed-loop against the mount's own encoders (D9), never a timed motion.
    /// </summary>
    Task SlewToCoordinatesAsync(double raDegrees, double decDegrees, CancellationToken cancellationToken = default);

    /// <summary>
    /// Driver-reported pier side. Advisory only (D9) -- callers must also
    /// compute hour angle independently rather than trusting this alone.
    /// </summary>
    Task<PierSide> GetSideOfPierAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// The mount's configured site location. Source and accuracy are the
    /// caller's concern (D14: mount driver, GPS, or manual entry, in that
    /// order of preference); this contract just exposes whatever the driver
    /// currently reports.
    /// </summary>
    Task<GeodeticLocation> GetSiteLocationAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Writes a site to the mount, only ever because the user asked (D35).
    ///
    /// Throws when the driver refuses. There is no capability flag to ask
    /// first, so trying is the only test. Returning normally does not mean the
    /// mount holds exactly this site, since a driver may round it or keep only
    /// some of it. Callers read it back with <see cref="GetSiteLocationAsync"/>.
    /// </summary>
    Task SetSiteLocationAsync(GeodeticLocation site, CancellationToken cancellationToken = default);

    /// <summary>
    /// Stops the mount: ends any slew where it has got to, then turns tracking
    /// off. This is what the operator's Abort means (D36): cancelling a slew
    /// alone leaves the drive running, which is not "stop".
    ///
    /// Throws when tracking could not be turned off, with a message that says
    /// so. The slew is stopped regardless, since that is the part that can hit
    /// something.
    /// </summary>
    Task HaltAsync(CancellationToken cancellationToken = default);
}
