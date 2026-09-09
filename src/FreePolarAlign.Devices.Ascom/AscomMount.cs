using System.Runtime.InteropServices;

namespace FreePolarAlign.Devices.Ascom;

/// <summary>
/// <see cref="IMount"/> over a late-bound ASCOM <c>ITelescope</c> COM driver
/// (see <see cref="AscomDeviceProvider"/> for why late-bound). UNVERIFIED
/// against a real driver -- see that class's doc comment and
/// docs/MOUNT-COMPATIBILITY.md.
///
/// Known contract/ASCOM mismatch (worth recording since <see cref="IMount"/>
/// is frozen): <see cref="IMount.SlewToCoordinatesAsync"/> is documented as
/// taking absolute J2000 coordinates, but ASCOM's <c>ITelescope.SlewToCoordinatesAsync</c>
/// interprets its RA/Dec arguments according to whatever the driver reports
/// via its own <c>EquatorialSystem</c> property (J2000, JNow/topocentric,
/// B1950, ...), which is a per-driver, sometimes per-mount-setting choice.
/// There is no ASCOM call that means "always J2000 regardless of driver
/// configuration". This class refuses to guess: it slews only when the
/// driver reports <c>equJ2000</c>, and throws <see cref="NotSupportedException"/>
/// otherwise, naming the driver's actual coordinate system so the failure is
/// diagnosable rather than a silently-wrong slew. A real integration would
/// need either a driver configured for J2000 (many EQMOD/SynScan setups
/// default to JNow) or a J2000-to-apparent conversion at this layer.
/// </summary>
public sealed class AscomMount : IMount
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(250);

    private readonly string _progId;
    private readonly dynamic _telescope;
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
        try
        {
            double raHours = (double)_telescope.RightAscension;
            double decDegrees = (double)_telescope.Declination;
            PierSide pierSide = TryGetSideOfPier();
            var position = new MountPosition(AscomMapping.RaHoursToDegrees(raHours), decDegrees, pierSide, DateTime.UtcNow);
            return Task.FromResult(position);
        }
        catch (Exception ex)
        {
            throw new AscomPlatformNotAvailableException($"Failed to read position from ASCOM mount '{_progId}': {ex.Message}", ex);
        }
    }

    public async Task SlewToCoordinatesAsync(double raDegrees, double decDegrees, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (!CanSlewAsync)
        {
            throw new NotSupportedException($"ASCOM mount '{_progId}' does not report CanSlewAsync; it is not usable in automatic mode (D9).");
        }

        int equatorialSystem;
        try
        {
            equatorialSystem = (int)_telescope.EquatorialSystem;
        }
        catch (Exception ex)
        {
            throw new AscomPlatformNotAvailableException($"Failed to read EquatorialSystem from ASCOM mount '{_progId}': {ex.Message}", ex);
        }

        if (!AscomMapping.IsJ2000(equatorialSystem))
        {
            throw new NotSupportedException(
                $"ASCOM mount '{_progId}' reports EquatorialSystem={equatorialSystem}, not equJ2000. " +
                "This contract slews in J2000 coordinates only -- reconfigure the driver for J2000, or see " +
                "this class's doc comment for the underlying ASCOM/contract mismatch.");
        }

        double raHours = AscomMapping.RaDegreesToHours(raDegrees);
        try
        {
            _telescope.SlewToCoordinatesAsync(raHours, decDegrees);
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
