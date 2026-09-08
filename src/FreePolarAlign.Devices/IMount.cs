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

/// <summary>Geodetic observer location. Latitude accuracy is a hard requirement (D14).</summary>
public sealed record GeodeticLocation(double LatitudeDegrees, double LongitudeDegrees, double HeightMeters);

/// <summary>A mount's reported pointing position at a moment in time.</summary>
public sealed record MountPosition(double RaDegrees, double DecDegrees, PierSide PierSide, DateTime TimestampUtc);

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
}
