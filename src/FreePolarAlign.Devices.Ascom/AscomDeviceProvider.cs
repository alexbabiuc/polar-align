using System.Runtime.InteropServices;

namespace FreePolarAlign.Devices.Ascom;

/// <summary>
/// D4/D9's ASCOM provider. Talks to the ASCOM Platform via late-bound COM
/// (<see cref="Type.GetTypeFromProgID(string)"/> + <c>dynamic</c>) rather than
/// a compile-time reference to <c>ASCOM.DriverAccess</c>.
///
/// Why late-bound: the ASCOM Platform is a Windows-only installer product,
/// not something published as a portable NuGet package -- there is no
/// package this project could reference and have compile against a real
/// ASCOM assembly on the macOS build machine (D1). Late binding needs
/// nothing but the BCL's COM interop support, so it compiles identically
/// whether or not the Platform (or any driver) is present, and the actual
/// COM calls only need to succeed on the Windows machine this runs on. The
/// cost is no compile-time checking of member names/signatures against the
/// real ASCOM interfaces -- a risk this project accepts explicitly because
/// there is no way to verify a compile-time binding here either; both paths
/// are "compiles, never executed" until run against real Windows hardware.
///
/// UNVERIFIED: this entire class has never executed against the real ASCOM
/// Platform or a real driver -- this development machine is macOS with
/// neither installed. It compiles under net10.0-windows (EnableWindowsTargeting).
/// See docs/MOUNT-COMPATIBILITY.md and the implementing report for exactly
/// what is and is not exercised by the test suite.
/// </summary>
public sealed class AscomDeviceProvider : IDeviceProvider
{
    private const string ProfileProgId = "ASCOM.Utilities.Profile";

    public string Name => "ASCOM";

    public string Version => typeof(AscomDeviceProvider).Assembly.GetName().Version?.ToString() ?? "0.0.0";

    public IReadOnlyList<DeviceDescriptor> DiscoverCameras() => DiscoverDevices("Camera");

    public IReadOnlyList<DeviceDescriptor> DiscoverMounts() => DiscoverDevices("Telescope");

    public ICamera OpenCamera(string deviceId) => new AscomCamera(deviceId);

    public IMount OpenMount(string deviceId) => new AscomMount(deviceId);

    /// <summary>
    /// Enumerates registered ASCOM drivers of one device type via the
    /// Platform's <c>ASCOM.Utilities.Profile</c> COM object, whose
    /// <c>RegisteredDevices(deviceType)</c> method returns an
    /// (ArrayList of) key/value pairs of ProgID and friendly name. If the
    /// Platform is not installed, <c>Profile</c>'s ProgID will not resolve at
    /// all -- exactly the case D4 calls out: this must throw a readable
    /// <see cref="AscomPlatformNotAvailableException"/>, not return an empty list.
    /// </summary>
    private static IReadOnlyList<DeviceDescriptor> DiscoverDevices(string ascomDeviceType)
    {
        object profile = CreateComObject(ProfileProgId,
            "The ASCOM Platform does not appear to be installed on this machine " +
            "(its Profile component is not registered). Install it from https://ascom-standards.org/ " +
            "before discovering ASCOM devices.");

        try
        {
            dynamic dynamicProfile = profile;
            var descriptors = new List<DeviceDescriptor>();
            foreach (dynamic entry in dynamicProfile.RegisteredDevices(ascomDeviceType))
            {
                string progId = (string)entry.Key;
                string friendlyName = (string)entry.Value;
                descriptors.Add(new DeviceDescriptor(progId, friendlyName, progId));
            }

            return descriptors;
        }
        catch (Exception ex) when (ex is not AscomPlatformNotAvailableException)
        {
            throw new AscomPlatformNotAvailableException(
                $"Failed to enumerate registered ASCOM '{ascomDeviceType}' devices via the Profile COM object: {ex.Message}", ex);
        }
        finally
        {
            if (Marshal.IsComObject(profile))
            {
                Marshal.ReleaseComObject(profile);
            }
        }
    }

    internal static object CreateComObject(string progId, string notAvailableHint)
    {
        Type? type;
        try
        {
            type = Type.GetTypeFromProgID(progId, throwOnError: false);
        }
        catch (Exception ex)
        {
            throw new AscomPlatformNotAvailableException($"Could not resolve COM ProgID '{progId}': {ex.Message} {notAvailableHint}", ex);
        }

        if (type is null)
        {
            throw new AscomPlatformNotAvailableException($"COM ProgID '{progId}' is not registered on this machine. {notAvailableHint}");
        }

        try
        {
            return Activator.CreateInstance(type)
                ?? throw new AscomPlatformNotAvailableException($"Activator.CreateInstance('{progId}') returned null.");
        }
        catch (Exception ex) when (ex is not AscomPlatformNotAvailableException)
        {
            throw new AscomPlatformNotAvailableException($"Could not create an instance of COM ProgID '{progId}': {ex.Message}", ex);
        }
    }
}
