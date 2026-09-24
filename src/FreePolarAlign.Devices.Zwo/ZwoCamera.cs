using System.Runtime.InteropServices;

namespace FreePolarAlign.Devices.Zwo;

/// <summary>
/// One ZWO camera, driven through the SDK's snapshot calls:
/// <c>ASIStartExposure</c>, poll <c>ASIGetExpStatus</c>, then
/// <c>ASIGetDataAfterExp</c>. The video-mode calls are not used; a polar
/// alignment takes one frame at a time, and snapshot mode gives each frame a
/// definite start, which is what its timestamp is computed from.
///
/// Full sensor, no binning, always. The plate solve wants every star the sensor
/// can see, and the pixel scale the session computes assumes unbinned pixels.
///
/// Colour cameras are read out as raw Bayer data and written as they come. The
/// solver finds stars in a Bayer frame well enough, and debayering is a
/// decision for later rather than a guess made here.
/// </summary>
public sealed class ZwoCamera : ICamera
{
    /// <summary>How often the exposure status is asked for. Short beside any exposure worth taking, long beside the cost of the call.</summary>
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(20);

    /// <summary>
    /// Allowance beyond the exposure itself for readout and USB transfer. An
    /// ASI290 at 16 bits is 4 MB, well under a second on USB 2; this covers a
    /// slow hub with room to spare, and it is the difference between a hung
    /// camera being reported and the application waiting forever.
    /// </summary>
    private static readonly TimeSpan ReadoutAllowance = TimeSpan.FromSeconds(30);

    private readonly int _cameraId;
    private readonly string _workingDirectory;
    private readonly List<(int ImageType, CameraReadoutMode Mode)> _modes = new();
    private int _modeIndex;
    private CameraGainRange? _gainRange;
    private bool _disposed;

    internal unsafe ZwoCamera(AsiNative.CameraInfo info, string deviceId, string workingDirectory)
    {
        _cameraId = info.CameraId;
        _workingDirectory = workingDirectory;

        Name = ZwoMapping.ReadCString(new ReadOnlySpan<byte>(info.Name, 64));
        if (Name.Length == 0)
        {
            Name = deviceId;
        }

        SensorWidthPixels = (int)info.MaxWidth.Value;
        SensorHeightPixels = (int)info.MaxHeight.Value;
        PixelSizeMicrons = info.PixelSize;

        // Only the two raw formats. RGB24 and Y8 are the SDK's processed
        // outputs, and a plate solve wants the sensor's own values.
        bool raw8 = false;
        bool raw16 = false;
        for (int i = 0; i < 8; i++)
        {
            int format = info.SupportedVideoFormat[i];
            if (format == AsiNative.ImageEnd)
            {
                break;
            }

            raw8 |= format == AsiNative.ImageRaw8;
            raw16 |= format == AsiNative.ImageRaw16;
        }

        if (raw8)
        {
            _modes.Add((AsiNative.ImageRaw8, new CameraReadoutMode(_modes.Count, "RAW8", 8)));
        }

        if (raw16)
        {
            _modes.Add((AsiNative.ImageRaw16, new CameraReadoutMode(_modes.Count, "RAW16", 16)));
        }

        if (_modes.Count == 0)
        {
            throw new NotSupportedException($"ZWO camera '{Name}' offers neither RAW8 nor RAW16 output.");
        }

        // Sixteen bits by default: an ASI290 digitises at 12, and throwing four
        // of them away limits how finely a faint star's centre can be measured.
        // A remembered choice is re-applied after connecting.
        _modeIndex = _modes.Count - 1;
    }

    public string Name { get; }

    public int SensorWidthPixels { get; }

    public int SensorHeightPixels { get; }

    public double PixelSizeMicrons { get; }

    public bool IsConnected { get; private set; }

    public string? UniqueId { get; private set; }

    public IReadOnlyList<CameraReadoutMode> ReadoutModes =>
        _modes.Count > 1 ? _modes.Select(m => m.Mode).ToArray() : Array.Empty<CameraReadoutMode>();

    public int? ReadoutModeIndex => _modes.Count > 1 ? _modeIndex : null;

    public CameraGainRange? GainRange => IsConnected ? _gainRange : null;

    public int? Gain
    {
        get
        {
            if (!IsConnected || _gainRange is null)
            {
                return null;
            }

            return ReadGain();
        }
    }

    public Task ConnectAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        cancellationToken.ThrowIfCancellationRequested();

        ZwoDeviceProvider.Library.EnsureLoaded();

        Check(AsiNative.ASIOpenCamera(_cameraId), "open the camera");
        try
        {
            Check(AsiNative.ASIInitCamera(_cameraId), "initialise the camera");
            _gainRange = ReadGainRange();
            UniqueId = ReadSerialNumber();
            ApplyFormat(_modes[_modeIndex].ImageType);

            // Gain under manual control, at whatever it currently is. Auto gain
            // would change the noise from frame to frame under a fit that
            // weights them all alike.
            if (_gainRange is not null && Gain is { } current)
            {
                Check(AsiNative.ASISetControlValue(_cameraId, AsiNative.ControlGain, new CLong(current), 0), "set the gain");
            }
        }
        catch
        {
            AsiNative.ASICloseCamera(_cameraId);
            throw;
        }

        IsConnected = true;
        return Task.CompletedTask;
    }

    public Task DisconnectAsync(CancellationToken cancellationToken = default)
    {
        if (IsConnected)
        {
            IsConnected = false;
            AsiNative.ASICloseCamera(_cameraId);
        }

        return Task.CompletedTask;
    }

    public Task SetReadoutModeAsync(int index, CancellationToken cancellationToken = default)
    {
        if (index < 0 || index >= _modes.Count)
        {
            throw new ArgumentOutOfRangeException(
                nameof(index), index, $"ZWO camera '{Name}' offers {_modes.Count} readout mode(s).");
        }

        if (IsConnected)
        {
            ApplyFormat(_modes[index].ImageType);
        }

        _modeIndex = index;
        return Task.CompletedTask;
    }

    public Task SetGainAsync(int value, CancellationToken cancellationToken = default)
    {
        if (!IsConnected || _gainRange is not { } range)
        {
            throw new InvalidOperationException($"ZWO camera '{Name}' is not connected, or reports no gain control.");
        }

        Check(
            AsiNative.ASISetControlValue(_cameraId, AsiNative.ControlGain, new CLong(range.Clamp(value)), 0),
            "set the gain");
        return Task.CompletedTask;
    }

    public async Task<CapturedImage> ExposeAsync(
        TimeSpan duration, CaptureContext? context = null, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!IsConnected)
        {
            throw new InvalidOperationException($"ZWO camera '{Name}' is not connected.");
        }

        cancellationToken.ThrowIfCancellationRequested();

        (int imageType, CameraReadoutMode mode) = _modes[_modeIndex];
        int bytesPerPixel = imageType == AsiNative.ImageRaw16 ? 2 : 1;

        Check(
            AsiNative.ASISetControlValue(
                _cameraId, AsiNative.ControlExposure, new CLong(checked((nint)ZwoMapping.ExposureMicroseconds(duration))), 0),
            "set the exposure");

        DateTime startUtc = DateTime.UtcNow;
        Check(AsiNative.ASIStartExposure(_cameraId, 0), "start the exposure");

        try
        {
            await WaitForExposureAsync(duration, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            // Cancelled or failed: the camera is told, so the next exposure does
            // not meet one still running.
            AsiNative.ASIStopExposure(_cameraId);
            throw;
        }

        byte[] buffer = new byte[checked(SensorWidthPixels * SensorHeightPixels * bytesPerPixel)];
        Download(buffer);

        double[,] pixels = NativeFrame.ToPixels(buffer, SensorWidthPixels, SensorHeightPixels, bytesPerPixel);
        return NativeFrame.Write(this, pixels, mode.BitDepth ?? 16, duration, startUtc, context, _workingDirectory);
    }

    private async Task WaitForExposureAsync(TimeSpan duration, CancellationToken cancellationToken)
    {
        DateTime deadline = DateTime.UtcNow + duration + ReadoutAllowance;

        // Most of the wait is the exposure itself, so sleep through it rather
        // than poll through it.
        if (duration > PollInterval)
        {
            await Task.Delay(duration - PollInterval, cancellationToken).ConfigureAwait(false);
        }

        while (true)
        {
            switch (ReadExposureStatus())
            {
                case AsiNative.ExposureSuccess:
                    return;
                case AsiNative.ExposureFailed:
                    throw new InvalidOperationException($"ZWO camera '{Name}' reported the exposure failed.");
                case AsiNative.ExposureIdle:
                    throw new InvalidOperationException(
                        $"ZWO camera '{Name}' went idle without finishing the exposure. Was it opened by another program?");
            }

            if (DateTime.UtcNow > deadline)
            {
                throw new TimeoutException(
                    $"ZWO camera '{Name}' had not finished a {duration.TotalSeconds:0.###} s exposure " +
                    $"{ReadoutAllowance.TotalSeconds:0} s after it should have.");
            }

            await Task.Delay(PollInterval, cancellationToken).ConfigureAwait(false);
        }
    }

    private unsafe int? ReadGain()
    {
        CLong value;
        int isAuto;
        return AsiNative.ASIGetControlValue(_cameraId, AsiNative.ControlGain, &value, &isAuto) == AsiNative.Success
            ? (int)value.Value
            : null;
    }

    private unsafe int ReadExposureStatus()
    {
        int status;
        Check(AsiNative.ASIGetExpStatus(_cameraId, &status), "read the exposure status");
        return status;
    }

    private unsafe void Download(byte[] buffer)
    {
        fixed (byte* pointer = buffer)
        {
            Check(AsiNative.ASIGetDataAfterExp(_cameraId, pointer, new CLong(buffer.Length)), "download the frame");
        }
    }

    private void ApplyFormat(int imageType) =>
        Check(AsiNative.ASISetROIFormat(_cameraId, SensorWidthPixels, SensorHeightPixels, 1, imageType), "set the frame format");

    /// <summary>
    /// The gain range from the camera's own control table. Read, never assumed:
    /// ZWO models differ (an ASI290 runs 0 to 600, others to 400 or 570), and a
    /// hardcoded table would be wrong for the next model released.
    /// </summary>
    private unsafe CameraGainRange? ReadGainRange()
    {
        int count;
        if (AsiNative.ASIGetNumOfControls(_cameraId, &count) != AsiNative.Success)
        {
            return null;
        }

        for (int index = 0; index < count; index++)
        {
            AsiNative.ControlCaps caps;
            if (AsiNative.ASIGetControlCaps(_cameraId, index, &caps) != AsiNative.Success)
            {
                continue;
            }

            if (caps.ControlType == AsiNative.ControlGain && caps.IsWritable != 0)
            {
                int minimum = (int)caps.MinValue.Value;
                int maximum = (int)caps.MaxValue.Value;
                return maximum > minimum ? new CameraGainRange(minimum, maximum) : null;
            }
        }

        return null;
    }

    private unsafe string? ReadSerialNumber()
    {
        AsiNative.SerialNumber serial;
        return AsiNative.ASIGetSerialNumber(_cameraId, &serial) == AsiNative.Success
            ? ZwoMapping.FormatSerialNumber(new ReadOnlySpan<byte>(serial.Id, 8))
            : null;
    }

    private void Check(int result, string action)
    {
        if (result != AsiNative.Success)
        {
            throw new InvalidOperationException(
                $"ZWO camera '{Name}' could not {action}: {AsiNative.ErrorName(result)}.");
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        if (IsConnected)
        {
            IsConnected = false;
            AsiNative.ASICloseCamera(_cameraId);
        }
    }
}
