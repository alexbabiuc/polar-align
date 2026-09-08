namespace FreePolarAlign.Devices;

/// <summary>
/// One completed exposure. <paramref name="ExposureMidpointUtc"/> is the
/// timestamp that matters for this project (README: "Sidereal tracking is
/// harmless... we... timestamp exposure midpoints"), not start or end time.
/// </summary>
public sealed record CapturedImage(string FitsPath, DateTime ExposureMidpointUtc, TimeSpan Duration);

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

    Task ConnectAsync(CancellationToken cancellationToken = default);

    Task DisconnectAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Starts and waits out one exposure, returning the captured frame written
    /// to disk as FITS. The FITS file's own WCS keywords (if any) are provider
    /// output, not solver input; solving happens separately via ISolver.
    /// </summary>
    Task<CapturedImage> ExposeAsync(TimeSpan duration, CancellationToken cancellationToken = default);
}
