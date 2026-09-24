using System.Runtime.InteropServices;

namespace FreePolarAlign.Devices.ToupTek;

/// <summary>
/// P/Invoke declarations for the subset of ToupTek's <c>toupcam</c> SDK this
/// plugin uses, transcribed from <c>toupcam.h</c>.
///
/// The default calling convention is used on purpose: the header declares
/// <c>__stdcall</c> on Windows and nothing elsewhere, which is exactly what
/// <see cref="CallingConvention.Winapi"/> means (and on x64 there is only one
/// convention anyway).
///
/// Strings differ by platform too -- <c>wchar_t</c> on Windows, UTF-8
/// <c>char</c> elsewhere -- so <c>Toupcam_Open</c> is declared twice and the
/// enumeration array is parsed by hand at offsets checked against the real
/// header for both ABIs (see <see cref="ToupTekMapping"/>).
/// </summary>
internal static unsafe class ToupcamNative
{
    public const string LibraryName = "toupcam";

    public const uint Max = 128;

    // TOUPCAM_OPTION_xxx
    public const uint OptionRaw = 0x04;
    public const uint OptionBitDepth = 0x06;
    public const uint OptionTrigger = 0x0b;

    // TOUPCAM_EVENT_xxx
    public const uint EventImage = 0x0004;
    public const uint EventTriggerFail = 0x0007;
    public const uint EventError = 0x0080;
    public const uint EventDisconnected = 0x0081;
    public const uint EventNoFrameTimeout = 0x0082;

    [StructLayout(LayoutKind.Sequential)]
    public struct FrameInfoV2
    {
        public uint Width;
        public uint Height;
        public uint Flag;
        public uint Sequence;
        public ulong Timestamp;
    }

    [DllImport(LibraryName)]
    public static extern uint Toupcam_EnumV2(byte* devices);

    [DllImport(LibraryName, EntryPoint = "Toupcam_Open", CharSet = CharSet.Unicode)]
    public static extern IntPtr OpenWide(string cameraId);

    [DllImport(LibraryName, EntryPoint = "Toupcam_Open")]
    public static extern IntPtr OpenNarrow([MarshalAs(UnmanagedType.LPUTF8Str)] string cameraId);

    [DllImport(LibraryName)]
    public static extern void Toupcam_Close(IntPtr handle);

    [DllImport(LibraryName)]
    public static extern int Toupcam_StartPullModeWithCallback(
        IntPtr handle, delegate* unmanaged<uint, IntPtr, void> callback, IntPtr context);

    [DllImport(LibraryName)]
    public static extern int Toupcam_Stop(IntPtr handle);

    [DllImport(LibraryName)]
    public static extern int Toupcam_Trigger(IntPtr handle, ushort number);

    [DllImport(LibraryName)]
    public static extern int Toupcam_PullImageV2(IntPtr handle, void* imageData, int bits, FrameInfoV2* info);

    [DllImport(LibraryName)]
    public static extern int Toupcam_put_ExpoTime(IntPtr handle, uint microseconds);

    [DllImport(LibraryName)]
    public static extern int Toupcam_put_AutoExpoEnable(IntPtr handle, int mode);

    [DllImport(LibraryName)]
    public static extern int Toupcam_get_ExpoAGainRange(IntPtr handle, ushort* minimum, ushort* maximum, ushort* defaultValue);

    [DllImport(LibraryName)]
    public static extern int Toupcam_get_ExpoAGain(IntPtr handle, ushort* gain);

    [DllImport(LibraryName)]
    public static extern int Toupcam_put_ExpoAGain(IntPtr handle, ushort gain);

    [DllImport(LibraryName)]
    public static extern int Toupcam_get_SerialNumber(IntPtr handle, byte* serialNumber);

    [DllImport(LibraryName)]
    public static extern int Toupcam_put_Option(IntPtr handle, uint option, int value);

    [DllImport(LibraryName)]
    public static extern int Toupcam_get_Size(IntPtr handle, int* width, int* height);

    [DllImport(LibraryName)]
    public static extern int Toupcam_get_MaxBitDepth(IntPtr handle);

    [DllImport(LibraryName)]
    public static extern int Toupcam_get_PixelSize(IntPtr handle, uint resolutionIndex, float* x, float* y);

    public static IntPtr Open(string cameraId) =>
        OperatingSystem.IsWindows() ? OpenWide(cameraId) : OpenNarrow(cameraId);

    /// <summary>HRESULT convention: negative is failure, and S_FALSE (1) is a success.</summary>
    public static bool Failed(int result) => result < 0;
}
