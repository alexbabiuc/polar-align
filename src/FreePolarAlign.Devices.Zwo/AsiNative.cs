using System.Runtime.InteropServices;

namespace FreePolarAlign.Devices.Zwo;

/// <summary>
/// P/Invoke declarations for the subset of ZWO's <c>ASICamera2</c> SDK this
/// plugin uses, transcribed from <c>ASICamera2.h</c>.
///
/// The structs carry C <c>long</c> fields, which are 32 bits on Windows and 64
/// on macOS and Linux, so they are declared with <see cref="CLong"/> and their
/// size genuinely differs by platform. Both layouts were checked against the
/// real header with the C compiler before this was written -- 240 and 248 bytes
/// for the camera info, 248 and 264 for the control caps -- and a test pins the
/// size for whichever platform the tests run on. A wrong layout here does not
/// fail loudly: the SDK writes past the end of the struct.
/// </summary>
internal static unsafe class AsiNative
{
    public const string LibraryName = "ASICamera2";

    public const int Success = 0;

    // ASI_IMG_TYPE
    public const int ImageRaw8 = 0;
    public const int ImageRaw16 = 2;
    public const int ImageEnd = -1;

    // ASI_CONTROL_TYPE
    public const int ControlGain = 0;
    public const int ControlExposure = 1;

    // ASI_EXPOSURE_STATUS
    public const int ExposureIdle = 0;
    public const int ExposureWorking = 1;
    public const int ExposureSuccess = 2;
    public const int ExposureFailed = 3;

    [StructLayout(LayoutKind.Sequential)]
    public struct CameraInfo
    {
        public fixed byte Name[64];
        public int CameraId;
        public CLong MaxHeight;
        public CLong MaxWidth;
        public int IsColorCam;
        public int BayerPattern;
        public fixed int SupportedBins[16];
        public fixed int SupportedVideoFormat[8];
        public double PixelSize;
        public int MechanicalShutter;
        public int St4Port;
        public int IsCoolerCam;
        public int IsUsb3Host;
        public int IsUsb3Camera;
        public float ElecPerAdu;
        public int BitDepth;
        public int IsTriggerCam;
        public fixed byte Unused[16];
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct ControlCaps
    {
        public fixed byte Name[64];
        public fixed byte Description[128];
        public CLong MaxValue;
        public CLong MinValue;
        public CLong DefaultValue;
        public int IsAutoSupported;
        public int IsWritable;
        public int ControlType;
        public fixed byte Unused[32];
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct SerialNumber
    {
        public fixed byte Id[8];
    }

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
    public static extern int ASIGetNumOfConnectedCameras();

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
    public static extern int ASIGetCameraProperty(CameraInfo* info, int cameraIndex);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
    public static extern int ASIOpenCamera(int cameraId);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
    public static extern int ASIInitCamera(int cameraId);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
    public static extern int ASICloseCamera(int cameraId);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
    public static extern int ASIGetNumOfControls(int cameraId, int* count);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
    public static extern int ASIGetControlCaps(int cameraId, int controlIndex, ControlCaps* caps);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
    public static extern int ASIGetControlValue(int cameraId, int controlType, CLong* value, int* isAuto);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
    public static extern int ASISetControlValue(int cameraId, int controlType, CLong value, int isAuto);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
    public static extern int ASISetROIFormat(int cameraId, int width, int height, int bin, int imageType);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
    public static extern int ASIStartExposure(int cameraId, int isDark);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
    public static extern int ASIStopExposure(int cameraId);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
    public static extern int ASIGetExpStatus(int cameraId, int* status);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
    public static extern int ASIGetDataAfterExp(int cameraId, byte* buffer, CLong bufferSize);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
    public static extern int ASIGetSerialNumber(int cameraId, SerialNumber* serialNumber);

    /// <summary>The SDK's error names, for messages a user can search for.</summary>
    public static string ErrorName(int code) => code switch
    {
        0 => "ASI_SUCCESS",
        1 => "ASI_ERROR_INVALID_INDEX",
        2 => "ASI_ERROR_INVALID_ID",
        3 => "ASI_ERROR_INVALID_CONTROL_TYPE",
        4 => "ASI_ERROR_CAMERA_CLOSED",
        5 => "ASI_ERROR_CAMERA_REMOVED",
        8 => "ASI_ERROR_INVALID_SIZE",
        9 => "ASI_ERROR_INVALID_IMGTYPE",
        10 => "ASI_ERROR_OUTOF_BOUNDARY",
        11 => "ASI_ERROR_TIMEOUT",
        12 => "ASI_ERROR_INVALID_SEQUENCE",
        13 => "ASI_ERROR_BUFFER_TOO_SMALL",
        14 => "ASI_ERROR_VIDEO_MODE_ACTIVE",
        15 => "ASI_ERROR_EXPOSURE_IN_PROGRESS",
        16 => "ASI_ERROR_GENERAL_ERROR",
        17 => "ASI_ERROR_INVALID_MODE",
        _ => $"ASI error {code}",
    };
}
