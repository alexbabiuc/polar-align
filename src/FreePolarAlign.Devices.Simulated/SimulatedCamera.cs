using FreePolarAlign.Core.Astrometry;
using FreePolarAlign.Devices.Simulated.SyntheticSky;
using FreePolarAlign.Imaging.Fits;
using FreePolarAlign.Imaging.Wcs;

namespace FreePolarAlign.Devices.Simulated;

/// <param name="FocalLengthMillimetres">
/// The instrument's real focal length. Note that nothing downstream is told
/// this: the software has to measure it from a solve, which is what makes the
/// focal-length recovery claim testable.
/// </param>
public sealed record SimulatedCameraOptions(
    int WidthPixels = 2737,
    int HeightPixels = 2053,
    double PixelSizeMicrons = 3.8,
    double FocalLengthMillimetres = 100.0,
    double CameraRotationDegrees = 23.0,
    bool Mirrored = false,
    ObservingConditions? Conditions = null,
    AtmosphericConditions? Atmosphere = null,
    int RandomSeed = 1);

/// <summary>
/// A camera bolted to a <see cref="SimulatedMount"/>, imaging a real star
/// catalogue through wherever that mount is actually pointing.
///
/// The frame it writes carries no WCS. That is deliberate and load-bearing: the
/// simulator knows the true plate solution, and writing it into the header would
/// let a "blind" solve read the answer instead of finding it. A real camera does
/// not know where it is pointing either.
/// </summary>
public sealed class SimulatedCamera : ICamera
{
    private readonly SimulatedMount _mount;
    private readonly StarCatalog _catalog;
    private readonly ObservingConditions _conditions;
    private readonly AtmosphericConditions _atmosphere;
    private readonly string _workingDirectory;
    private int _exposureCount;

    public SimulatedCamera(SimulatedMount mount, StarCatalog catalog, SimulatedCameraOptions options, string? workingDirectory = null)
    {
        ArgumentNullException.ThrowIfNull(mount);
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(options);

        _mount = mount;
        _catalog = catalog;
        Options = options;
        _conditions = options.Conditions ?? ObservingConditions.Nominal;
        _atmosphere = options.Atmosphere ?? AtmosphericConditions.Standard;
        _workingDirectory = workingDirectory ?? Path.Combine(Path.GetTempPath(), $"fpa-sim-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_workingDirectory);
    }

    public SimulatedCameraOptions Options { get; }

    public string Name => "Simulated Camera";

    public int SensorWidthPixels => Options.WidthPixels;

    public int SensorHeightPixels => Options.HeightPixels;

    public double PixelSizeMicrons => Options.PixelSizeMicrons;

    public bool IsConnected { get; private set; }

    /// <summary>Simulates the camera dropping off the bus mid-sequence.</summary>
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

    public async Task<CapturedImage> ExposeAsync(TimeSpan duration, CancellationToken cancellationToken = default)
    {
        if (!IsConnected)
        {
            throw new InvalidOperationException("The simulated camera is not connected.");
        }

        DateTime start = DateTime.UtcNow;
        if (duration > TimeSpan.Zero)
        {
            // Shortened deliberately: the exposure has to be interruptible and
            // has to take some real time, but a test suite cannot wait out
            // hundreds of real exposures.
            await Task.Delay(TimeSpan.FromMilliseconds(Math.Min(duration.TotalMilliseconds, 20.0)), cancellationToken)
                .ConfigureAwait(false);
        }

        cancellationToken.ThrowIfCancellationRequested();
        if (!IsConnected)
        {
            throw new InvalidOperationException("The simulated camera disconnected during the exposure.");
        }

        DateTime midpoint = start + duration / 2.0;

        // Where the telescope really points, and therefore which patch of sky
        // lands on the sensor. The catalogue position of the star at the centre
        // is the *unrefracted* one, so the inverse transform runs with the
        // atmosphere in place (D15).
        HorizontalCoordinates pointing = _mount.PhysicalPointingAt(midpoint);
        (double ra, double dec) = TopocentricConverter.FromAltAz(
            pointing, midpoint, ToObserverSite(await _mount.GetSiteLocationAsync(cancellationToken).ConfigureAwait(false)), _atmosphere);

        TanWcsSolution trueWcs = SkyRenderer.BuildWcs(
            ra, dec, Options.WidthPixels, Options.HeightPixels,
            Options.PixelSizeMicrons, Options.FocalLengthMillimetres,
            Options.CameraRotationDegrees, Options.Mirrored);

        FitsImage rendered = SkyRenderer.Render(
            _catalog, trueWcs, Options.WidthPixels, Options.HeightPixels, _conditions,
            new Random(Options.RandomSeed + Interlocked.Increment(ref _exposureCount)));

        FitsImage frame = WithoutPlateSolution(rendered, duration);

        string path = Path.Combine(_workingDirectory, $"capture-{_exposureCount:D4}.fits");
        FitsFile.Write(path, frame);

        return new CapturedImage(path, midpoint, duration);
    }

    /// <summary>
    /// Strips every WCS keyword the renderer wrote. Without this the simulator
    /// would be handing the solver the answer, and every solve in the project's
    /// test suite would be meaningless.
    /// </summary>
    private static FitsImage WithoutPlateSolution(FitsImage rendered, TimeSpan duration)
    {
        var header = new FitsHeader();
        foreach (FitsCard card in rendered.ExtraHeader.Cards)
        {
            if (card.Value is null || IsPlateSolution(card.Keyword))
            {
                continue;
            }

            header.Set(card.Keyword, card.Value, card.Comment);
        }

        header.Set("EXPTIME", duration.TotalSeconds, "exposure, seconds");

        return new FitsImage(
            rendered.Width, rendered.Height, rendered.BitPix, rendered.Bzero, rendered.Bscale, rendered.Pixels, header);
    }

    private static bool IsPlateSolution(string keyword) =>
        keyword.StartsWith("CTYPE", StringComparison.Ordinal)
        || keyword.StartsWith("CRPIX", StringComparison.Ordinal)
        || keyword.StartsWith("CRVAL", StringComparison.Ordinal)
        || keyword.StartsWith("CUNIT", StringComparison.Ordinal)
        || keyword.StartsWith("CD1_", StringComparison.Ordinal)
        || keyword.StartsWith("CD2_", StringComparison.Ordinal)
        || keyword.StartsWith("CDELT", StringComparison.Ordinal)
        || keyword.StartsWith("CROTA", StringComparison.Ordinal);

    private static ObserverSite ToObserverSite(GeodeticLocation location) =>
        new(location.LatitudeDegrees, location.LongitudeDegrees, location.HeightMeters);

    public void Dispose()
    {
        IsConnected = false;
        try
        {
            if (Directory.Exists(_workingDirectory))
            {
                Directory.Delete(_workingDirectory, recursive: true);
            }
        }
        catch (IOException)
        {
            // A leftover temp directory is not worth failing a dispose over.
        }
    }
}
