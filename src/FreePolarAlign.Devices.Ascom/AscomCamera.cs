using System.Runtime.InteropServices;
using FreePolarAlign.Imaging.Fits;

namespace FreePolarAlign.Devices.Ascom;

/// <summary>
/// <see cref="ICamera"/> over a late-bound ASCOM <c>ICameraV3</c> COM driver
/// (see <see cref="AscomDeviceProvider"/> for why late-bound). UNVERIFIED
/// against a real driver -- see that class's doc comment and
/// docs/MOUNT-COMPATIBILITY.md.
///
/// Assumes a monochrome sensor: <c>ImageArray</c> is expected to be a 2D
/// array (ASCOM's convention is <c>[x, y]</c>, i.e. dimension 0 is the column
/// index); a 3D (colour/Bayer) result throws <see cref="NotSupportedException"/>
/// rather than silently discarding channels, since a guide/polar-alignment
/// camera is assumed monochrome by the rest of this project (D13's envelope
/// is framed entirely in terms of a mono guide scope sensor).
/// </summary>
public sealed class AscomCamera : ICamera
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(200);

    private readonly string _progId;
    private readonly dynamic _camera;
    private readonly string _workingDirectory;
    private bool _disposed;

    public AscomCamera(string progId)
        : this(progId, Path.Combine(Path.GetTempPath(), "FreePolarAlign", "captures"))
    {
    }

    internal AscomCamera(string progId, string workingDirectory)
    {
        _progId = progId;
        _workingDirectory = workingDirectory;
        _camera = AscomDeviceProvider.CreateComObject(progId,
            $"Is the ASCOM driver for this camera ('{progId}') installed?");
    }

    public string Name => _progId;

    public int SensorWidthPixels
    {
        get
        {
            try
            {
                return (int)_camera.CameraXSize;
            }
            catch (Exception ex)
            {
                throw new AscomPlatformNotAvailableException($"Failed to read CameraXSize from ASCOM camera '{_progId}': {ex.Message}", ex);
            }
        }
    }

    public int SensorHeightPixels
    {
        get
        {
            try
            {
                return (int)_camera.CameraYSize;
            }
            catch (Exception ex)
            {
                throw new AscomPlatformNotAvailableException($"Failed to read CameraYSize from ASCOM camera '{_progId}': {ex.Message}", ex);
            }
        }
    }

    public double PixelSizeMicrons
    {
        get
        {
            try
            {
                return (double)_camera.PixelSizeX;
            }
            catch (Exception ex)
            {
                throw new AscomPlatformNotAvailableException($"Failed to read PixelSizeX from ASCOM camera '{_progId}': {ex.Message}", ex);
            }
        }
    }

    public bool IsConnected { get; private set; }

    public Task ConnectAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            _camera.Connected = true;
        }
        catch (Exception ex)
        {
            throw new AscomPlatformNotAvailableException($"Failed to connect to ASCOM camera '{_progId}': {ex.Message}", ex);
        }

        IsConnected = true;
        return Task.CompletedTask;
    }

    public Task DisconnectAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            _camera.Connected = false;
        }
        finally
        {
            IsConnected = false;
        }

        return Task.CompletedTask;
    }

    public async Task<CapturedImage> ExposeAsync(TimeSpan duration, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        DateTime startUtc = DateTime.UtcNow;
        try
        {
            _camera.StartExposure(duration.TotalSeconds, true);
        }
        catch (Exception ex)
        {
            throw new AscomPlatformNotAvailableException($"ASCOM camera '{_progId}' rejected StartExposure: {ex.Message}", ex);
        }

        try
        {
            while (!IsImageReady())
            {
                await Task.Delay(PollInterval, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            TryAbortExposure();
            throw;
        }

        Array imageArray;
        try
        {
            imageArray = (Array)_camera.ImageArray;
        }
        catch (Exception ex)
        {
            throw new AscomPlatformNotAvailableException($"Failed to read ImageArray from ASCOM camera '{_progId}': {ex.Message}", ex);
        }

        FitsImage fitsImage = BuildFitsImage(imageArray);

        Directory.CreateDirectory(_workingDirectory);
        string path = Path.Combine(_workingDirectory, $"{startUtc:yyyyMMdd_HHmmss}_{Guid.NewGuid():N}.fits");
        FitsFile.Write(path, fitsImage);

        DateTime midpointUtc = AscomMapping.ComputeExposureMidpointUtc(startUtc, duration);
        return new CapturedImage(path, midpointUtc, duration);
    }

    private bool IsImageReady()
    {
        try
        {
            return (bool)_camera.ImageReady;
        }
        catch (Exception ex)
        {
            throw new AscomPlatformNotAvailableException($"Failed to read ImageReady from ASCOM camera '{_progId}': {ex.Message}", ex);
        }
    }

    private void TryAbortExposure()
    {
        try
        {
            _camera.AbortExposure();
        }
        catch (Exception)
        {
            // Best-effort: the caller is already unwinding via the
            // OperationCanceledException that triggered this call.
        }
    }

    /// <summary>
    /// Converts an ASCOM <c>ImageArray</c> (2D, indexed <c>[x, y]</c>) into a
    /// <see cref="FitsImage"/> (indexed <c>[y, x]</c> per that type's own
    /// convention). BITPIX is fixed at 32-bit integer: ASCOM's ImageArray is
    /// documented to return whichever integer width the driver's SafeArray
    /// uses (commonly Int32), and widening everything to Int32 loses nothing
    /// for real sensor bit depths (typically 8-16 bits).
    /// </summary>
    private static FitsImage BuildFitsImage(Array imageArray)
    {
        if (imageArray.Rank != 2)
        {
            throw new NotSupportedException(
                $"ASCOM camera returned a rank-{imageArray.Rank} ImageArray; only monochrome (rank 2) images are supported.");
        }

        int width = imageArray.GetLength(0);
        int height = imageArray.GetLength(1);
        var pixels = new double[height, width];

        for (int x = 0; x < width; x++)
        {
            for (int y = 0; y < height; y++)
            {
                object? raw = imageArray.GetValue(x, y);
                pixels[y, x] = raw is null ? 0.0 : Convert.ToDouble(raw);
            }
        }

        return new FitsImage(width, height, FitsBitPix.Int32, 0.0, 1.0, pixels);
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
                _camera.Connected = false;
            }
        }
        catch (Exception)
        {
            // Best-effort on teardown.
        }

        if (Marshal.IsComObject(_camera))
        {
            Marshal.ReleaseComObject(_camera);
        }

        _disposed = true;
    }
}
