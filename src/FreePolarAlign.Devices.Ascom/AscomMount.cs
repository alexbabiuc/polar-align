using System.Runtime.InteropServices;
using FreePolarAlign.Core.Astrometry;

namespace FreePolarAlign.Devices.Ascom;

/// <summary>
/// <see cref="IMount"/> over a late-bound ASCOM <c>ITelescope</c> COM driver
/// (see <see cref="AscomDeviceProvider"/> for why late-bound). UNVERIFIED
/// against a real driver -- see that class's doc comment and
/// docs/DEVICE-COMPATIBILITY.md.
///
/// Contract/ASCOM epoch mismatch (worth recording since <see cref="IMount"/>
/// is frozen): <see cref="IMount.SlewToCoordinatesAsync"/> is documented as
/// taking absolute J2000 coordinates, but ASCOM's <c>ITelescope.SlewToCoordinatesAsync</c>
/// interprets its RA/Dec arguments -- and reports <c>RightAscension</c>/
/// <c>Declination</c> -- according to whatever the driver says via its own
/// <c>EquatorialSystem</c> property (J2000, JNow/topocentric, B1950, ...),
/// which is a per-driver, sometimes per-mount-setting choice. There is no
/// ASCOM call that means "always J2000 regardless of driver configuration",
/// so this class translates at the boundary instead:
///
/// <list type="bullet">
/// <item><c>equJ2000</c>: coordinates pass through untouched.</item>
/// <item><c>equTopocentric</c> (JNow -- the default on many EQMOD and SynScan
/// setups): converted with <see cref="ApparentPlace"/>, J2000 to apparent on
/// the way out and apparent to J2000 on the way back, so that every caller
/// still sees only J2000 as the contract promises. Ignoring the difference
/// would shift the commanded position by the full precession offset, about 22
/// arcminutes at the current epoch, and by a different amount at each hour
/// angle -- which is exactly the declination drift D16 exists to prevent.</item>
/// <item>Anything else (<c>equOther</c>, <c>equJ2050</c>, <c>equB1950</c>):
/// <see cref="NotSupportedException"/>, naming the driver's actual system.
/// These are rare enough in practice that a guess is worse than a diagnosable
/// refusal.</item>
/// </list>
/// </summary>
public sealed class AscomMount : IMount
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(250);

    private readonly string _progId;
    private readonly dynamic _telescope;
    private AscomEquatorialSystem? _equatorialSystem;
    private bool _disposed;

    public AscomMount(string progId)
    {
        _progId = progId;
        _telescope = AscomDeviceProvider.CreateComObject(progId,
            $"Is the ASCOM driver for this mount ('{progId}') installed?");
    }

    public string Name => _progId;

    public bool IsConnected { get; private set; }

    public bool CanSlewAsync
    {
        get
        {
            try
            {
                return (bool)_telescope.CanSlewAsync;
            }
            catch (Exception)
            {
                // A driver that does not even expose the capability flag is treated as
                // not supporting it -- D9: "a mount lacking it is unusable in automatic mode".
                return false;
            }
        }
    }

    public Task ConnectAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            _telescope.Connected = true;
        }
        catch (Exception ex)
        {
            throw new AscomPlatformNotAvailableException($"Failed to connect to ASCOM mount '{_progId}': {ex.Message}", ex);
        }

        IsConnected = true;

        // ASCOM only guarantees EquatorialSystem is readable while connected, and
        // a driver's setting can change between sessions (it is usually a
        // checkbox in the driver's setup dialog), so the cache is per connection.
        _equatorialSystem = null;
        return Task.CompletedTask;
    }

    public Task DisconnectAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            _telescope.Connected = false;
        }
        finally
        {
            IsConnected = false;
        }

        return Task.CompletedTask;
    }

    public Task<MountPosition> GetPositionAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        // Outside the try below, so a driver in an unsupported system is reported
        // as exactly that rather than as a driver that has stopped responding.
        AscomEquatorialSystem system = ReadEquatorialSystem();

        double raDriverDegrees;
        double decDriverDegrees;
        PierSide pierSide;
        DateTime timestampUtc;
        TrackingState tracking;
        try
        {
            raDriverDegrees = AscomMapping.RaHoursToDegrees((double)_telescope.RightAscension);
            decDriverDegrees = (double)_telescope.Declination;
            pierSide = TryGetSideOfPier();
            tracking = TryGetTracking();
            timestampUtc = DateTime.UtcNow;
        }
        catch (Exception ex)
        {
            throw new AscomPlatformNotAvailableException($"Failed to read position from ASCOM mount '{_progId}': {ex.Message}", ex);
        }

        // The driver reports in its own equatorial system; MountPosition is J2000
        // (see the class doc comment), so a JNow driver's reading is converted
        // back. The conversion uses the timestamp carried on the position, so the
        // coordinates and the instant they are stamped with agree.
        var (raDegrees, decDegrees) = ToJ2000(system, raDriverDegrees, decDriverDegrees, timestampUtc);

        return Task.FromResult(new MountPosition(raDegrees, decDegrees, pierSide, timestampUtc, tracking));
    }

    public async Task SlewToCoordinatesAsync(double raDegrees, double decDegrees, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (!CanSlewAsync)
        {
            throw new NotSupportedException($"ASCOM mount '{_progId}' does not report CanSlewAsync; it is not usable in automatic mode (D9).");
        }

        var (commandedRaDegrees, commandedDecDegrees) = FromJ2000(
            ReadEquatorialSystem(), raDegrees, decDegrees, DateTime.UtcNow);
        double raHours = AscomMapping.RaDegreesToHours(commandedRaDegrees);
        try
        {
            _telescope.SlewToCoordinatesAsync(raHours, commandedDecDegrees);
        }
        catch (Exception ex)
        {
            throw new AscomPlatformNotAvailableException($"ASCOM mount '{_progId}' rejected SlewToCoordinatesAsync: {ex.Message}", ex);
        }

        try
        {
            while (IsSlewing())
            {
                await Task.Delay(PollInterval, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            TryAbortSlew();
            throw;
        }
    }

    public Task<PierSide> GetSideOfPierAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(TryGetSideOfPier());
    }

    public Task<GeodeticLocation> GetSiteLocationAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            double latitude = (double)_telescope.SiteLatitude;
            double longitude = (double)_telescope.SiteLongitude;
            double elevation = (double)_telescope.SiteElevation;
            return Task.FromResult(new GeodeticLocation(latitude, longitude, elevation));
        }
        catch (Exception ex)
        {
            // Deliberately NOT advisory (unlike SideOfPier): D14 needs to know
            // when the preferred site source (mount driver) is unavailable so
            // the caller falls through to GPS/manual entry rather than silently
            // using a stale or default value.
            throw new AscomPlatformNotAvailableException($"ASCOM mount '{_progId}' does not report a usable site location: {ex.Message}", ex);
        }
    }

    /// <summary>
    /// The equatorial system the driver says it speaks, cached for the lifetime
    /// of the connection.
    ///
    /// Cached because every read is an out-of-process COM call and this one is
    /// on the path of the status poll that runs several times a second, while
    /// the answer is a driver configuration setting rather than anything that
    /// varies with the sky. <see cref="ConnectAsync"/> clears the cache, so
    /// changing the setting and reconnecting is enough to pick it up.
    /// </summary>
    private AscomEquatorialSystem ReadEquatorialSystem()
    {
        if (_equatorialSystem is { } cached)
        {
            return cached;
        }

        int raw;
        try
        {
            raw = (int)_telescope.EquatorialSystem;
        }
        catch (Exception ex)
        {
            throw new AscomPlatformNotAvailableException($"Failed to read EquatorialSystem from ASCOM mount '{_progId}': {ex.Message}", ex);
        }

        AscomEquatorialSystem system = AscomMapping.MapEquatorialSystem(raw);
        if (system is not (AscomEquatorialSystem.J2000 or AscomEquatorialSystem.Topocentric))
        {
            throw new NotSupportedException(
                $"ASCOM mount '{_progId}' reports EquatorialSystem={raw} ({system}), which this driver layer " +
                "cannot translate. Only equJ2000 and equTopocentric (JNow) are supported -- reconfigure the " +
                "driver for one of those, or see this class's doc comment for the underlying ASCOM/contract mismatch.");
        }

        _equatorialSystem = system;
        return system;
    }

    /// <summary>J2000 (what the contract speaks) to whatever the driver expects.</summary>
    private static (double RaDegrees, double DecDegrees) FromJ2000(
        AscomEquatorialSystem system, double raDegrees, double decDegrees, DateTime utc) =>
        system == AscomEquatorialSystem.Topocentric
            ? ApparentPlace.FromJ2000(raDegrees, decDegrees, utc)
            : (raDegrees, decDegrees);

    /// <summary>Whatever the driver reports back to J2000, the inverse of <see cref="FromJ2000"/>.</summary>
    private static (double RaDegrees, double DecDegrees) ToJ2000(
        AscomEquatorialSystem system, double raDegrees, double decDegrees, DateTime utc) =>
        system == AscomEquatorialSystem.Topocentric
            ? ApparentPlace.ToJ2000(raDegrees, decDegrees, utc)
            : (raDegrees, decDegrees);

    private bool IsSlewing()
    {
        try
        {
            return (bool)_telescope.Slewing;
        }
        catch (Exception)
        {
            // If a driver cannot even report Slewing mid-command, there is no
            // safe way to keep waiting; treat the slew as complete rather than
            // spinning forever.
            return false;
        }
    }

    private void TryAbortSlew()
    {
        try
        {
            _telescope.AbortSlew();
        }
        catch (Exception)
        {
            // Best-effort: a mount that also fails to abort has bigger problems
            // than this call can report, and the caller is already unwinding
            // via the OperationCanceledException this method was invoked from.
        }
    }

    private PierSide TryGetSideOfPier()
    {
        try
        {
            return AscomMapping.MapPierSide((int)_telescope.SideOfPier);
        }
        catch (Exception)
        {
            // D9: advisory only. Some drivers (historically SynScan) do not
            // implement this reliably; never fail a caller over it.
            return PierSide.Unknown;
        }
    }

    /// <summary>
    /// Whether the drive is running. <c>Tracking</c> is a required ASCOM
    /// property, but "required" and "implemented" are not the same thing in this
    /// ecosystem, so a driver that throws yields Unknown rather than an
    /// exception -- and Unknown rather than false, since a stopped drive and an
    /// unreporting one look identical in a boolean and mean quite different
    /// things to someone watching the numbers.
    /// </summary>
    private TrackingState TryGetTracking()
    {
        try
        {
            return (bool)_telescope.Tracking ? TrackingState.Tracking : TrackingState.Stopped;
        }
        catch (Exception)
        {
            return TrackingState.Unknown;
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        try
        {
            if (IsConnected)
            {
                _telescope.Connected = false;
            }
        }
        catch (Exception)
        {
            // Best-effort on teardown.
        }

        if (Marshal.IsComObject(_telescope))
        {
            Marshal.ReleaseComObject(_telescope);
        }

        _disposed = true;
    }
}
