namespace FreePolarAlign.Devices;

/// <summary>
/// One device a provider can open, discovered before connection. Kept
/// deliberately thin: identification only, no capability negotiation, since
/// that lives on the opened <see cref="ICamera"/>/<see cref="IMount"/>.
/// </summary>
public sealed record DeviceDescriptor(string Id, string DisplayName, string DriverInfo);

/// <summary>
/// The runtime plugin contract (D4). Providers are discovered by scanning
/// <c>plugins/</c> at startup and are never referenced at compile time by the
/// application assembly. A provider that fails to load (e.g. a missing ASCOM
/// Platform) must surface a readable diagnostic through normal .NET exception
/// mechanisms during discovery/open rather than silently omitting devices
/// (D4: "Plugin load failures must be surfaced clearly, not swallowed").
/// </summary>
public interface IDeviceProvider
{
    /// <summary>Human-readable provider name (e.g. "ASCOM", "Simulator").</summary>
    string Name { get; }

    /// <summary>Provider (not device driver) version, for diagnostics and compatibility notes.</summary>
    string Version { get; }

    IReadOnlyList<DeviceDescriptor> DiscoverCameras();

    IReadOnlyList<DeviceDescriptor> DiscoverMounts();

    ICamera OpenCamera(string deviceId);

    IMount OpenMount(string deviceId);
}
