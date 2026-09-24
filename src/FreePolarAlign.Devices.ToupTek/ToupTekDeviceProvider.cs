using FreePolarAlign.Devices.Plugins;

namespace FreePolarAlign.Devices.ToupTek;

/// <summary>
/// ToupTek cameras through ToupTek's own SDK, without ASCOM -- the same reason
/// as the ZWO plugin: gain can be set from here, where through ASCOM it cannot.
///
/// ToupTek also builds cameras sold under other names (Altair, Omegon,
/// RisingCam, Bresser among them). Whether a given rebrand enumerates through
/// <c>toupcam</c> or only through its own renamed copy of the library depends
/// on the model and the SDK version, and is not assumed here: this plugin
/// loads <c>toupcam</c> and lists whatever it finds.
///
/// UNVERIFIED against a real camera, for the same reason as the ZWO plugin.
/// </summary>
public sealed unsafe class ToupTekDeviceProvider : IDeviceProvider
{
    internal static readonly NativeLibraryLocator Library = new(
        typeof(ToupTekDeviceProvider).Assembly,
        ToupcamNative.LibraryName,
        "ToupTek",
        windowsFile: "toupcam.dll",
        macFile: "libtoupcam.dylib",
        linuxFile: "libtoupcam.so");

    private readonly string _workingDirectory;

    public ToupTekDeviceProvider()
        : this(NativeFrame.DefaultWorkingDirectory())
    {
    }

    internal ToupTekDeviceProvider(string workingDirectory) => _workingDirectory = workingDirectory;

    public string Name => "ToupTek";

    public string Version => typeof(ToupTekDeviceProvider).Assembly.GetName().Version?.ToString() ?? "0.0.0";

    /// <summary>Cameras only; the SDK drives nothing else.</summary>
    public IReadOnlyList<DeviceDescriptor> DiscoverDevices()
    {
        IReadOnlyList<ToupTekMapping.EnumeratedCamera> cameras = Enumerate();
        IReadOnlyList<string> names = DeviceNaming.Disambiguate(cameras.Select(c => c.DisplayName).ToArray());

        // The SDK's own id is the device id: it is what Toupcam_Open takes, and
        // unlike an index it does not change when another camera is plugged in.
        return cameras
            .Select((camera, i) => new CameraDescriptor(
                camera.Id, names[i], "ToupTek SDK", HasSetupDialog: false, HasGainControl: true))
            .ToArray();
    }

    public ICamera OpenCamera(string deviceId)
    {
        ToupTekMapping.EnumeratedCamera? camera = Enumerate()
            .FirstOrDefault(c => string.Equals(c.Id, deviceId, StringComparison.Ordinal));

        return camera is null
            ? throw new InvalidOperationException(
                $"No ToupTek camera with id '{deviceId}' is connected. Is it plugged in, and not open in another program?")
            : new ToupTekCamera(camera.Id, camera.DisplayName, _workingDirectory);
    }

    public IMount OpenMount(string deviceId) =>
        throw new NotSupportedException("The ToupTek camera SDK does not drive mounts.");

    private static IReadOnlyList<ToupTekMapping.EnumeratedCamera> Enumerate()
    {
        Library.EnsureLoaded();

        bool wide = OperatingSystem.IsWindows();
        int stride = ToupTekMapping.DeviceLayout(wide).Stride;
        byte[] buffer = new byte[stride * (int)ToupcamNative.Max];

        uint count;
        fixed (byte* pointer = buffer)
        {
            count = ToupcamNative.Toupcam_EnumV2(pointer);
        }

        return ToupTekMapping.ParseDevices(buffer, (int)Math.Min(count, ToupcamNative.Max), wide);
    }
}
