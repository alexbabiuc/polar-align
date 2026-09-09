using FreePolarAlign.Devices.Simulated.SyntheticSky;

namespace FreePolarAlign.Devices.Simulated;

/// <summary>
/// The virtual observatory as a device provider (D4), so the session and UI
/// drive it through exactly the same contract they drive real hardware through.
///
/// That is the point rather than a convenience: it means the whole alignment
/// loop can be developed and demonstrated on a laptop at noon, and that a
/// regression in the session logic shows up here rather than on a cold night at
/// a telescope.
/// </summary>
public sealed class SimulatedDeviceProvider : IDeviceProvider
{
    private readonly StarCatalog _catalog;
    private readonly SimulatedMountOptions _mountOptions;
    private readonly SimulatedCameraOptions _cameraOptions;

    private SimulatedMount? _mount;

    public SimulatedDeviceProvider(
        StarCatalog catalog,
        SimulatedMountOptions mountOptions,
        SimulatedCameraOptions? cameraOptions = null)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(mountOptions);

        _catalog = catalog;
        _mountOptions = mountOptions;
        _cameraOptions = cameraOptions ?? new SimulatedCameraOptions();
    }

    public string Name => "Simulator";

    public string Version => typeof(SimulatedDeviceProvider).Assembly.GetName().Version?.ToString() ?? "0.0.0";

    public IReadOnlyList<DeviceDescriptor> DiscoverCameras() =>
        new[] { new DeviceDescriptor("sim-camera", "Simulated Camera", "free-polar-align virtual observatory") };

    public IReadOnlyList<DeviceDescriptor> DiscoverMounts() =>
        new[] { new DeviceDescriptor("sim-mount", "Simulated Mount", "free-polar-align virtual observatory") };

    /// <summary>
    /// The camera is deliberately bound to this provider's mount instance: it
    /// images whatever that mount is pointing at, which is what closes the loop.
    /// Opening a camera therefore implies a mount.
    /// </summary>
    public ICamera OpenCamera(string deviceId)
    {
        if (deviceId != "sim-camera")
        {
            throw new ArgumentException($"Unknown simulated camera '{deviceId}'.", nameof(deviceId));
        }

        return new SimulatedCamera(MountInstance, _catalog, _cameraOptions);
    }

    public IMount OpenMount(string deviceId)
    {
        if (deviceId != "sim-mount")
        {
            throw new ArgumentException($"Unknown simulated mount '{deviceId}'.", nameof(deviceId));
        }

        return MountInstance;
    }

    /// <summary>The mount both devices share. Exposed so tests can read ground truth.</summary>
    public SimulatedMount MountInstance => _mount ??= new SimulatedMount(_mountOptions);
}
