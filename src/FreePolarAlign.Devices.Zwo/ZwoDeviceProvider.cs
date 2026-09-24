using FreePolarAlign.Devices.Plugins;

namespace FreePolarAlign.Devices.Zwo;

/// <summary>
/// ZWO ASI cameras through ZWO's own SDK, without ASCOM.
///
/// Worth having alongside the ASCOM path for one reason above the others: gain.
/// Through ASCOM the application cannot see or set it -- it lives behind the
/// driver's own window (D23) -- and a camera left at a high gain put the sky at
/// 57% of full well on the night this project most needed a solve. Through the
/// SDK it is one call, so it gets a control of its own.
///
/// The SDK is a native library this plugin does not ship; see
/// <see cref="NativeLibraryLocator"/> for where it is looked for, and why its
/// absence is logged rather than shown.
///
/// UNVERIFIED against a real camera: the development machine has neither the
/// camera nor the library. The struct layouts were checked against the real
/// header for both Windows and Unix ABIs, and everything that is not a native
/// call is tested.
/// </summary>
public sealed unsafe class ZwoDeviceProvider : IDeviceProvider
{
    internal static readonly NativeLibraryLocator Library = new(
        typeof(ZwoDeviceProvider).Assembly,
        AsiNative.LibraryName,
        "ZWO",
        windowsFile: "ASICamera2.dll",
        macFile: "libASICamera2.dylib",
        linuxFile: "libASICamera2.so");

    private readonly string _workingDirectory;

    public ZwoDeviceProvider()
        : this(NativeFrame.DefaultWorkingDirectory())
    {
    }

    internal ZwoDeviceProvider(string workingDirectory) => _workingDirectory = workingDirectory;

    public string Name => "ZWO";

    public string Version => typeof(ZwoDeviceProvider).Assembly.GetName().Version?.ToString() ?? "0.0.0";

    /// <summary>Cameras only. ZWO makes mounts, but not through this SDK.</summary>
    public IReadOnlyList<DeviceDescriptor> DiscoverDevices()
    {
        IReadOnlyList<(string Id, AsiNative.CameraInfo Info)> cameras = Enumerate();

        return cameras
            .Select(c => new CameraDescriptor(
                c.Id,
                c.Id,
                $"ZWO SDK, camera {c.Info.CameraId}",
                HasSetupDialog: false,
                HasGainControl: true))
            .ToArray();
    }

    public ICamera OpenCamera(string deviceId)
    {
        foreach ((string id, AsiNative.CameraInfo info) in Enumerate())
        {
            if (string.Equals(id, deviceId, StringComparison.Ordinal))
            {
                return new ZwoCamera(info, id, _workingDirectory);
            }
        }

        throw new InvalidOperationException(
            $"No ZWO camera called '{deviceId}' is connected. Is it plugged in, and not open in another program?");
    }

    public IMount OpenMount(string deviceId) =>
        throw new NotSupportedException("The ZWO camera SDK does not drive mounts.");

    /// <summary>
    /// Every connected camera, named as the picker shows it. Re-run on open
    /// rather than cached, because the SDK's camera ids are enumeration slots
    /// and change when a camera is unplugged.
    /// </summary>
    private static IReadOnlyList<(string Id, AsiNative.CameraInfo Info)> Enumerate()
    {
        Library.EnsureLoaded();

        int count = AsiNative.ASIGetNumOfConnectedCameras();
        var infos = new List<AsiNative.CameraInfo>(Math.Max(0, count));

        for (int index = 0; index < count; index++)
        {
            AsiNative.CameraInfo info;
            int result = AsiNative.ASIGetCameraProperty(&info, index);
            if (result != AsiNative.Success)
            {
                throw new InvalidOperationException(
                    $"The ZWO SDK could not describe camera {index}: {AsiNative.ErrorName(result)}.");
            }

            infos.Add(info);
        }

        IReadOnlyList<string> names = DeviceNaming.Disambiguate(
            infos.Select(i => ZwoMapping.ReadCString(new ReadOnlySpan<byte>(i.Name, 64))).ToArray());

        return infos.Select((info, i) => (names[i], info)).ToArray();
    }
}
