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

    /// <summary>
    /// The driver's readout modes, or empty when it offers none.
    ///
    /// ASCOM exposes these as an opaque list of names with no bit depth
    /// attached, so the depth is derived from <c>MaxADU</c> where that is
    /// unambiguous and left null otherwise. Deriving it from the mode's *name*
    /// was the obvious alternative and is rejected deliberately: a substring
    /// match on "8" would read "8-bit" correctly, "ADC 8x binned" wrongly, and
    /// there is no way to tell which happened. A wrong bit depth is worse than
    /// an unknown one, because the depth is what decides how far a centroid can
    /// be trusted.
    ///
    /// MaxADU is a camera-level property rather than a per-mode one, so this
    /// reports the depth of whatever mode is currently selected against every
    /// entry in the list. That is a known limitation of the standard, not of
    /// this mapping, and it is why the value is re-read after a mode change.
    /// </summary>
    public IReadOnlyList<CameraReadoutMode> ReadoutModes
    {
        get
        {
            string[] names;
            try
            {
                object raw = _camera.ReadoutModes;
                names = raw is System.Collections.IEnumerable enumerable
                    ? enumerable.Cast<object?>().Select(v => v?.ToString() ?? string.Empty).ToArray()
                    : Array.Empty<string>();
            }
            catch (Exception)
            {
                // ReadoutModes is only meaningful when CanFastReadout is false,
                // and drivers that do not implement it throw rather than return
                // an empty list. No modes is a normal answer, not a fault.
                return Array.Empty<CameraReadoutMode>();
            }

            if (names.Length <= 1)
            {
                // A single mode is not a choice, and offering it as one implies
                // a decision the user does not have to make.
                return Array.Empty<CameraReadoutMode>();
            }

            int? bitDepth = TryGetBitDepth();
            return names.Select((name, index) => new CameraReadoutMode(index, name, bitDepth)).ToArray();
        }
    }

    public int? ReadoutModeIndex
    {
        get
        {
            try
            {
                return (int)_camera.ReadoutMode;
            }
            catch (Exception)
            {
                return null;
            }
        }
    }

    public Task SetReadoutModeAsync(int index, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        IReadOnlyList<CameraReadoutMode> modes = ReadoutModes;
        if (index < 0 || index >= modes.Count)
        {
            throw new ArgumentOutOfRangeException(
                nameof(index), index, $"ASCOM camera '{_progId}' reports {modes.Count} readout mode(s).");
        }

        try
        {
            _camera.ReadoutMode = index;
        }
        catch (Exception ex)
        {
            throw new AscomPlatformNotAvailableException(
                $"Failed to set ReadoutMode on ASCOM camera '{_progId}': {ex.Message}", ex);
        }

        return Task.CompletedTask;
    }

    /// <summary>
    /// Bits per pixel implied by the camera's full-well value, where it implies
    /// one exactly. Anything that is not a power-of-two range less one is left
    /// unknown rather than rounded to the nearest plausible depth.
    /// </summary>
    private int? TryGetBitDepth()
    {
        int maxAdu;
        try
        {
            maxAdu = (int)_camera.MaxADU;
        }
        catch (Exception)
        {
            return null;
        }

        if (maxAdu <= 0)
        {
            return null;
        }

        long range = (long)maxAdu + 1;
        if ((range & (range - 1)) != 0)
        {
            // Not a power of two: some drivers report a saturation level rather
            // than a data range, and that says nothing about the bit depth.
            return null;
        }

        int bits = System.Numerics.BitOperations.TrailingZeroCount((ulong)range);
        return bits is >= 8 and <= 32 ? bits : null;
    }

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

    public async Task<CapturedImage> ExposeAsync(
        TimeSpan duration, CaptureContext? context = null, CancellationToken cancellationToken = default)
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

        // Asked per capture rather than cached: MaxADU tracks the selected
        // readout mode, so a mode change between exposures changes the answer.
        FitsImage fitsImage = BuildFitsImage(imageArray, TryGetBitDepth());
        DateTime midpointUtc = AscomMapping.ComputeExposureMidpointUtc(startUtc, duration);

        // Everything known about this frame at the moment it was taken, written
        // into it: a file recovered from a temp directory later is otherwise a
        // rectangle of numbers.
        FitsImage described = new(
            fitsImage.Width, fitsImage.Height, fitsImage.BitPix, fitsImage.Bzero, fitsImage.Bscale, fitsImage.Pixels,
            CaptureHeader.Build(fitsImage.ExtraHeader, this, duration, startUtc, midpointUtc, context));

        Directory.CreateDirectory(_workingDirectory);
        string path = Path.Combine(_workingDirectory, $"{startUtc:yyyyMMdd_HHmmss}_{Guid.NewGuid():N}.fits");
        FitsFile.Write(path, described);

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
    /// convention).
    ///
    /// The on-disk type is <see cref="FitsImage.ForCapturedFrame"/>'s business,
    /// given the depth the driver reports for the selected readout mode, and it
    /// matters more than it looks: this method used to fix BITPIX at 32 on
    /// the reasoning that ASCOM's ImageArray is an Int32 SafeArray and widening
    /// loses nothing. It loses no data, and it lost every solve -- Watney
    /// detects zero stars in a BITPIX 32 frame, so every capture against real
    /// hardware failed with NoStarsDetected while the simulator, which writes
    /// 16-bit, solved perfectly. See that method for the measurement.
    /// </summary>
    private static FitsImage BuildFitsImage(Array imageArray, int? bitsPerPixel)
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

        return FitsImage.ForCapturedFrame(width, height, pixels, bitsPerPixel);
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
