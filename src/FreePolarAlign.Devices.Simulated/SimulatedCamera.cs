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
    private int _readoutModeIndex;
    private int _gain;

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

    /// <summary>
    /// Two genuinely different readouts, not labels. Sixteen bits is what the
    /// renderer produces; eight really does quantise the frame to 256 levels and
    /// write it as such, so the cost of choosing it -- coarser centroids, and a
    /// sky background that may collapse into one or two levels on a short
    /// exposure -- shows up in the measurement rather than only in the menu.
    /// </summary>
    public IReadOnlyList<CameraReadoutMode> ReadoutModes { get; } = new[]
    {
        new CameraReadoutMode(0, "High dynamic range", 16),
        new CameraReadoutMode(1, "Fast", 8),
    };

    public int? ReadoutModeIndex => _readoutModeIndex;

    public Task SetReadoutModeAsync(int index, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (index < 0 || index >= ReadoutModes.Count)
        {
            throw new ArgumentOutOfRangeException(
                nameof(index), index, $"This camera has {ReadoutModes.Count} readout modes.");
        }

        _readoutModeIndex = index;
        return Task.CompletedTask;
    }

    /// <summary>
    /// A gain control shaped like a native SDK camera's (D25), so that what the
    /// engine does with gain -- the percentage mapping, a change mid-sequence
    /// reaching the next frame's header -- can be exercised without one. It
    /// does not change the rendered noise: nothing measured here depends on it,
    /// and a renderer that faked a gain curve would be a claim about sensors
    /// this project has not measured. Offered only while connected, as the
    /// native cameras offer it, because their range is read from the device.
    /// </summary>
    public CameraGainRange? GainRange => IsConnected ? SimulatedGainRange : null;

    public int? Gain => IsConnected ? Volatile.Read(ref _gain) : null;

    private static CameraGainRange SimulatedGainRange { get; } = new(0, 100);

    public Task SetGainAsync(int value, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!IsConnected)
        {
            throw new InvalidOperationException("The simulated camera is not connected.");
        }

        Volatile.Write(ref _gain, SimulatedGainRange.Clamp(value));
        return Task.CompletedTask;
    }

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

    public async Task<CapturedImage> ExposeAsync(
        TimeSpan duration, CaptureContext? context = null, CancellationToken cancellationToken = default)
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

        FitsImage frame = WithoutPlateSolution(AtReadoutDepth(rendered), duration);

        // The same provenance a real capture carries, so a simulated frame and
        // a real one can be looked at with the same tools and told apart by
        // what they say rather than by where they came from.
        frame = new FitsImage(
            frame.Width, frame.Height, frame.BitPix, frame.Bzero, frame.Bscale, frame.Pixels,
            CaptureHeader.Build(frame.ExtraHeader, this, duration, start, midpoint, context));

        string path = Path.Combine(_workingDirectory, $"capture-{_exposureCount:D4}.fits");
        FitsFile.Write(path, frame);

        return new CapturedImage(path, midpoint, duration);
    }

    /// <summary>
    /// Strips every WCS keyword the renderer wrote. Without this the simulator
    /// would be handing the solver the answer, and every solve in the project's
    /// test suite would be meaningless.
    /// </summary>
    /// <summary>
    /// Requantises the rendered frame to the selected readout depth.
    ///
    /// Written as genuine eight-bit data rather than eight-bit values in a
    /// sixteen-bit container, because the point of offering the mode is to be
    /// able to find out what it costs -- and one of the things it can cost is a
    /// solver or a header reader that handles the narrower format badly.
    /// </summary>
    private FitsImage AtReadoutDepth(FitsImage rendered)
    {
        CameraReadoutMode mode = ReadoutModes[_readoutModeIndex];
        if (mode.BitDepth is not 8)
        {
            return rendered;
        }

        // The renderer works in the full sixteen-bit well, so the whole range
        // maps onto 256 levels. That is what a camera switching to an eight-bit
        // readout does: the well is unchanged and the steps between levels get
        // 257 times coarser.
        const double LevelsPerStep = 65535.0 / 255.0;

        var quantised = new double[rendered.Height, rendered.Width];
        for (int y = 0; y < rendered.Height; y++)
        {
            for (int x = 0; x < rendered.Width; x++)
            {
                quantised[y, x] = Math.Clamp(Math.Round(rendered.Pixels[y, x] / LevelsPerStep), 0.0, 255.0);
            }
        }

        return new FitsImage(
            rendered.Width, rendered.Height, FitsBitPix.Byte,
            bzero: 0.0, bscale: 1.0, quantised, rendered.ExtraHeader);
    }

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
