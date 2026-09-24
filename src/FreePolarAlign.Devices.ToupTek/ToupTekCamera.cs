using System.Runtime.InteropServices;

namespace FreePolarAlign.Devices.ToupTek;

/// <summary>
/// One ToupTek camera, driven in software-trigger mode: each exposure is one
/// <c>Toupcam_Trigger(1)</c>, the SDK signals the frame through its event
/// callback, and <c>Toupcam_PullImageV2</c> collects it.
///
/// Trigger mode rather than the SDK's default free-running video, because a
/// frame taken from a video stream has no definite start -- it is whichever
/// frame happened to be in flight -- and the start is what the frame's
/// timestamp, and so the position the solve belongs to, is computed from.
///
/// RAW mode, so the frame is the sensor's own values: no white balance, no tone
/// curve, and for a colour camera the undebayered mosaic, written as it comes.
/// </summary>
public sealed class ToupTekCamera : ICamera
{
    /// <summary>Beyond the exposure itself, for readout and transfer; the difference between a hung camera being reported and a wait with no end.</summary>
    private static readonly TimeSpan ReadoutAllowance = TimeSpan.FromSeconds(30);

    private readonly string _cameraId;
    private readonly string _workingDirectory;
    private readonly object _gate = new();
    private IntPtr _handle;
    private GCHandle _self;
    private TaskCompletionSource? _frame;
    private int _maxBitDepth = 8;
    private int _modeIndex;
    private CameraGainRange? _gainRange;
    private bool _disposed;

    internal ToupTekCamera(string cameraId, string displayName, string workingDirectory)
    {
        _cameraId = cameraId;
        _workingDirectory = workingDirectory;
        Name = displayName;
    }

    public string Name { get; }

    public int SensorWidthPixels { get; private set; }

    public int SensorHeightPixels { get; private set; }

    public double PixelSizeMicrons { get; private set; }

    public bool IsConnected => _handle != IntPtr.Zero;

    public string? UniqueId { get; private set; }

    /// <summary>
    /// Eight bits, and the camera's full depth when it has more. The deeper mode
    /// is delivered in a 16-bit container whatever the ADC is, so it is labelled
    /// with the container depth that decides the file format (D20) and named
    /// with the ADC depth the user will recognise.
    /// </summary>
    public IReadOnlyList<CameraReadoutMode> ReadoutModes => _maxBitDepth > 8
        ? new[]
        {
            new CameraReadoutMode(0, "8-bit", 8),
            new CameraReadoutMode(1, $"{_maxBitDepth}-bit", 16),
        }
        : Array.Empty<CameraReadoutMode>();

    public int? ReadoutModeIndex => _maxBitDepth > 8 ? _modeIndex : null;

    public CameraGainRange? GainRange => IsConnected ? _gainRange : null;

    public int? Gain => IsConnected && _gainRange is not null ? ReadGain() : null;

    public unsafe Task ConnectAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        cancellationToken.ThrowIfCancellationRequested();

        if (IsConnected)
        {
            return Task.CompletedTask;
        }

        ToupTekDeviceProvider.Library.EnsureLoaded();

        IntPtr handle = ToupcamNative.Open(_cameraId);
        if (handle == IntPtr.Zero)
        {
            throw new InvalidOperationException(
                $"ToupTek camera '{Name}' could not be opened. Is it open in another program?");
        }

        try
        {
            // Every option that has to be set before streaming starts.
            Check(ToupcamNative.Toupcam_put_Option(handle, ToupcamNative.OptionRaw, 1), "switch to RAW output");

            int maxBitDepth = ToupcamNative.Toupcam_get_MaxBitDepth(handle);
            _maxBitDepth = maxBitDepth > 8 ? maxBitDepth : 8;

            // Full depth by default, for the same reason as the ZWO plugin: bits
            // thrown away limit how finely a faint star's centre is measured.
            _modeIndex = _maxBitDepth > 8 ? 1 : 0;
            Check(ToupcamNative.Toupcam_put_Option(handle, ToupcamNative.OptionBitDepth, _modeIndex), "set the bit depth");
            Check(ToupcamNative.Toupcam_put_AutoExpoEnable(handle, 0), "turn off auto exposure");
            Check(ToupcamNative.Toupcam_put_Option(handle, ToupcamNative.OptionTrigger, 1), "enter software trigger mode");

            int width;
            int height;
            Check(ToupcamNative.Toupcam_get_Size(handle, &width, &height), "read the frame size");
            SensorWidthPixels = width;
            SensorHeightPixels = height;

            float pixelX;
            float pixelY;
            PixelSizeMicrons = ToupcamNative.Failed(ToupcamNative.Toupcam_get_PixelSize(handle, 0, &pixelX, &pixelY))
                ? 0.0
                : pixelX;

            _gainRange = ReadGainRange(handle);
            UniqueId = ReadSerialNumber(handle);

            _self = GCHandle.Alloc(this);
            _handle = handle;
            StartStreaming();
        }
        catch
        {
            ReleaseHandle(handle);
            throw;
        }

        return Task.CompletedTask;
    }

    public Task DisconnectAsync(CancellationToken cancellationToken = default)
    {
        Close();
        return Task.CompletedTask;
    }

    public Task SetReadoutModeAsync(int index, CancellationToken cancellationToken = default)
    {
        int modes = _maxBitDepth > 8 ? 2 : 1;
        if (index < 0 || index >= modes)
        {
            throw new ArgumentOutOfRangeException(nameof(index), index, $"ToupTek camera '{Name}' offers {modes} readout mode(s).");
        }

        if (IsConnected && index != _modeIndex)
        {
            // Bit depth cannot change while the camera streams.
            Check(ToupcamNative.Toupcam_Stop(_handle), "stop streaming");
            Check(ToupcamNative.Toupcam_put_Option(_handle, ToupcamNative.OptionBitDepth, index), "set the bit depth");
            StartStreaming();
        }

        _modeIndex = index;
        return Task.CompletedTask;
    }

    public Task SetGainAsync(int value, CancellationToken cancellationToken = default)
    {
        if (!IsConnected || _gainRange is not { } range)
        {
            throw new InvalidOperationException($"ToupTek camera '{Name}' is not connected, or reports no gain control.");
        }

        Check(ToupcamNative.Toupcam_put_ExpoAGain(_handle, (ushort)range.Clamp(value)), "set the gain");
        return Task.CompletedTask;
    }

    public async Task<CapturedImage> ExposeAsync(
        TimeSpan duration, CaptureContext? context = null, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!IsConnected)
        {
            throw new InvalidOperationException($"ToupTek camera '{Name}' is not connected.");
        }

        cancellationToken.ThrowIfCancellationRequested();

        Check(ToupcamNative.Toupcam_put_ExpoTime(_handle, ToupTekMapping.ExposureMicroseconds(duration)), "set the exposure");

        var frame = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_gate)
        {
            _frame = frame;
        }

        DateTime startUtc = DateTime.UtcNow;
        Check(ToupcamNative.Toupcam_Trigger(_handle, 1), "trigger the exposure");

        try
        {
            await frame.Task.WaitAsync(duration + ReadoutAllowance, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is OperationCanceledException or TimeoutException)
        {
            // Cancel the pending trigger, so the next exposure does not collect
            // this one's frame.
            ToupcamNative.Toupcam_Trigger(_handle, 0);

            if (ex is TimeoutException)
            {
                throw new TimeoutException(
                    $"ToupTek camera '{Name}' had not delivered a {duration.TotalSeconds:0.###} s exposure " +
                    $"{ReadoutAllowance.TotalSeconds:0} s after it should have.",
                    ex);
            }

            throw;
        }
        finally
        {
            lock (_gate)
            {
                _frame = null;
            }
        }

        int bytesPerPixel = _modeIndex == 1 ? 2 : 1;
        (byte[] buffer, int width, int height) = Pull(bytesPerPixel);

        double[,] pixels = NativeFrame.ToPixels(buffer, width, height, bytesPerPixel);
        return NativeFrame.Write(this, pixels, bytesPerPixel * 8, duration, startUtc, context, _workingDirectory);
    }

    private unsafe (byte[] Buffer, int Width, int Height) Pull(int bytesPerPixel)
    {
        byte[] buffer = new byte[checked(SensorWidthPixels * SensorHeightPixels * bytesPerPixel)];
        ToupcamNative.FrameInfoV2 info;

        fixed (byte* pointer = buffer)
        {
            // bits is ignored in RAW mode; the buffer is packed, one or two
            // bytes per pixel, rows unpadded.
            Check(ToupcamNative.Toupcam_PullImageV2(_handle, pointer, 0, &info), "collect the frame");
        }

        return (buffer, (int)info.Width, (int)info.Height);
    }

    private unsafe void StartStreaming() =>
        Check(
            ToupcamNative.Toupcam_StartPullModeWithCallback(_handle, &OnEvent, GCHandle.ToIntPtr(_self)),
            "start the camera");

    /// <summary>
    /// The SDK's event callback, called on the SDK's own thread. It must never
    /// throw -- an exception unwinding into native code takes the process down
    /// -- and must never call back into the SDK, which the header says
    /// deadlocks. It only completes whatever exposure is waiting.
    /// </summary>
    [UnmanagedCallersOnly]
    private static void OnEvent(uint nEvent, IntPtr context)
    {
        try
        {
            if (context != IntPtr.Zero && GCHandle.FromIntPtr(context).Target is ToupTekCamera camera)
            {
                camera.Handle(nEvent);
            }
        }
        catch
        {
            // Nothing can be reported from here; the waiting exposure times out.
        }
    }

    private void Handle(uint nEvent)
    {
        TaskCompletionSource? frame;
        lock (_gate)
        {
            frame = _frame;
        }

        if (frame is null)
        {
            return;
        }

        switch (nEvent)
        {
            case ToupcamNative.EventImage:
                frame.TrySetResult();
                break;
            case ToupcamNative.EventTriggerFail:
            case ToupcamNative.EventError:
            case ToupcamNative.EventNoFrameTimeout:
                frame.TrySetException(new InvalidOperationException(
                    $"ToupTek camera '{Name}' reported event 0x{nEvent:x4} instead of a frame."));
                break;
            case ToupcamNative.EventDisconnected:
                frame.TrySetException(new InvalidOperationException($"ToupTek camera '{Name}' was disconnected."));
                break;
        }
    }

    private unsafe int? ReadGain()
    {
        ushort gain;
        return ToupcamNative.Failed(ToupcamNative.Toupcam_get_ExpoAGain(_handle, &gain)) ? null : gain;
    }

    /// <summary>
    /// From the camera, never assumed: ToupTek gain is in percent of unity and
    /// its range varies by sensor, from a few hundred to several thousand.
    /// </summary>
    private static unsafe CameraGainRange? ReadGainRange(IntPtr handle)
    {
        ushort minimum;
        ushort maximum;
        ushort defaultValue;
        if (ToupcamNative.Failed(ToupcamNative.Toupcam_get_ExpoAGainRange(handle, &minimum, &maximum, &defaultValue)))
        {
            return null;
        }

        return maximum > minimum ? new CameraGainRange(minimum, maximum) : null;
    }

    private static unsafe string? ReadSerialNumber(IntPtr handle)
    {
        byte* serial = stackalloc byte[32];
        new Span<byte>(serial, 32).Clear();
        return ToupcamNative.Failed(ToupcamNative.Toupcam_get_SerialNumber(handle, serial))
            ? null
            : ToupTekMapping.ReadSerialNumber(new ReadOnlySpan<byte>(serial, 32));
    }

    private void Check(int result, string action)
    {
        if (ToupcamNative.Failed(result))
        {
            throw new InvalidOperationException(
                $"ToupTek camera '{Name}' could not {action}: HRESULT 0x{unchecked((uint)result):x8}.");
        }
    }

    private void Close()
    {
        IntPtr handle = _handle;
        if (handle == IntPtr.Zero)
        {
            return;
        }

        _handle = IntPtr.Zero;
        ReleaseHandle(handle);
    }

    private void ReleaseHandle(IntPtr handle)
    {
        // The context handle is freed only after Close returns. The header
        // warns that calling Close from inside the callback deadlocks, which
        // says Close waits for the callback thread -- so once it returns, no
        // callback can still be holding the context. Inferred from that
        // warning, not stated outright.
        ToupcamNative.Toupcam_Close(handle);

        if (_self.IsAllocated)
        {
            _self.Free();
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        Close();
    }
}
