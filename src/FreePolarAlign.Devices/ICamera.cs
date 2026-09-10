namespace FreePolarAlign.Devices;

/// <summary>
/// One completed exposure. <paramref name="ExposureMidpointUtc"/> is the
/// timestamp that matters for this project (README: "Sidereal tracking is
/// harmless... we... timestamp exposure midpoints"), not start or end time.
/// </summary>
public sealed record CapturedImage(string FitsPath, DateTime ExposureMidpointUtc, TimeSpan Duration);

/// <summary>
/// One way a camera can be read out, as the driver describes it.
/// </summary>
/// <param name="Name">
/// The driver's own name for the mode, passed through verbatim. Inventing a
/// tidier one would mean guessing at what it does; the person who chose this
/// camera recognises the driver's wording.
/// </param>
/// <param name="BitDepth">
/// Bits per pixel, when the driver actually reveals it, and null otherwise.
/// Null is a real answer: the standard device interfaces expose readout modes
/// as opaque names, and claiming a depth that was inferred from a substring
/// match would be worse than admitting it is unknown -- the depth changes how
/// far a centroid can be trusted, so a wrong one is misleading rather than
/// merely unhelpful.
/// </param>
public sealed record CameraReadoutMode(int Index, string Name, int? BitDepth = null)
{
    public string Label => BitDepth is { } bits ? $"{Name} ({bits}-bit)" : Name;
}

/// <summary>
/// A camera device, opened via <see cref="IDeviceProvider.OpenCamera"/>.
/// Exposure is asynchronous because real cameras and subprocess/network
/// simulators alike take real time and must be cancellable mid-exposure.
/// </summary>
public interface ICamera : IDisposable
{
    string Name { get; }

    int SensorWidthPixels { get; }

    int SensorHeightPixels { get; }

    double PixelSizeMicrons { get; }

    bool IsConnected { get; }

    /// <summary>
    /// The readout modes this camera offers, or an empty list when it offers no
    /// choice. Empty is the common case and is not a failure -- most cameras
    /// have exactly one way of being read out, and a UI should say so rather
    /// than present a menu of one.
    /// </summary>
    IReadOnlyList<CameraReadoutMode> ReadoutModes { get; }

    /// <summary>
    /// Index of the mode now in use, or null if the camera has no selectable
    /// modes or will not say which is active.
    /// </summary>
    int? ReadoutModeIndex { get; }

    /// <summary>
    /// Selects a readout mode. Implementations must reject an index outside
    /// <see cref="ReadoutModes"/> rather than silently leaving the camera as it
    /// was: a user who believes they are reading out at sixteen bits when they
    /// are at eight has no way to discover it from the images.
    /// </summary>
    Task SetReadoutModeAsync(int index, CancellationToken cancellationToken = default);

    Task ConnectAsync(CancellationToken cancellationToken = default);

    Task DisconnectAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Starts and waits out one exposure, returning the captured frame written
    /// to disk as FITS. The FITS file's own WCS keywords (if any) are provider
    /// output, not solver input; solving happens separately via ISolver.
    /// </summary>
    Task<CapturedImage> ExposeAsync(TimeSpan duration, CancellationToken cancellationToken = default);
}
