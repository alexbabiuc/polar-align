using FreePolarAlign.Devices.Plugins;

namespace FreePolarAlign.Devices;

/// <summary>
/// One device the user can pick, qualified by the provider it came from.
///
/// The provider name is part of the identity rather than decoration: two
/// providers can perfectly well both offer a device called "Telescope", and a
/// selection that recorded only the device id would silently open the wrong one
/// the next time the plugin set changed.
/// </summary>
public sealed record DeviceOption(
    DeviceRole Kind,
    string ProviderName,
    string DeviceId,
    string DisplayName,
    string DriverInfo)
{
    /// <summary>What the picker shows. Provider included, for the reason above.</summary>
    public string Label => $"{DisplayName} ({ProviderName})";
}

/// <summary>Which list a <see cref="DeviceOption"/> belongs to.</summary>
public enum DeviceRole
{
    Camera,
    Mount
}

/// <summary>
/// Everything the user could choose to connect to, gathered from the built-in
/// providers plus whatever the plugin scan found (D4).
///
/// Discovery failures are carried alongside the devices rather than thrown,
/// because D4 is explicit that a broken or absent provider must produce a
/// readable message and not an empty list with no explanation. The distinction
/// between "no plugins directory" and "a plugin threw" is kept, since the first
/// is the normal state of a fresh install and showing it as a fault would train
/// the user to ignore the one warning that matters.
/// </summary>
public sealed class DeviceCatalog
{
    private readonly Dictionary<string, IDeviceProvider> _providers;

    private DeviceCatalog(
        Dictionary<string, IDeviceProvider> providers,
        IReadOnlyList<DeviceOption> cameras,
        IReadOnlyList<DeviceOption> mounts,
        IReadOnlyList<string> problems)
    {
        _providers = providers;
        Cameras = cameras;
        Mounts = mounts;
        Problems = problems;
    }

    public IReadOnlyList<DeviceOption> Cameras { get; }

    public IReadOnlyList<DeviceOption> Mounts { get; }

    /// <summary>
    /// Human-readable discovery problems, safe to show directly. Empty on a
    /// healthy install with no plugins.
    /// </summary>
    public IReadOnlyList<string> Problems { get; }

    /// <summary>
    /// Builds a catalogue from providers supplied directly (the simulator, which
    /// is always available) and a plugins directory to scan.
    /// </summary>
    /// <param name="pluginsRootDirectory">
    /// Scanned only if it exists. A missing directory is the ordinary case for a
    /// simulator-only install and is not reported as a problem.
    /// </param>
    public static DeviceCatalog Create(
        IEnumerable<IDeviceProvider> builtInProviders,
        string? pluginsRootDirectory = null)
    {
        ArgumentNullException.ThrowIfNull(builtInProviders);

        var providers = new List<IDeviceProvider>(builtInProviders);
        var problems = new List<string>();

        if (!string.IsNullOrWhiteSpace(pluginsRootDirectory) && Directory.Exists(pluginsRootDirectory))
        {
            PluginLoadResult loaded = PluginLoader.Load(pluginsRootDirectory);
            providers.AddRange(loaded.Providers);
            problems.AddRange(loaded.Failures.Select(f => f.Message));
        }

        var byName = new Dictionary<string, IDeviceProvider>(StringComparer.Ordinal);
        var cameras = new List<DeviceOption>();
        var mounts = new List<DeviceOption>();

        foreach (IDeviceProvider provider in providers)
        {
            if (!byName.TryAdd(provider.Name, provider))
            {
                problems.Add(
                    $"Two providers both call themselves '{provider.Name}'; only the first is usable, " +
                    "because a stored device selection could not tell them apart.");
                continue;
            }

            Collect(provider, DeviceRole.Camera, cameras, problems);
            Collect(provider, DeviceRole.Mount, mounts, problems);
        }

        return new DeviceCatalog(byName, cameras, mounts, problems);
    }

    public ICamera OpenCamera(DeviceOption option)
    {
        ArgumentNullException.ThrowIfNull(option);
        return Provider(option.ProviderName).OpenCamera(option.DeviceId);
    }

    public IMount OpenMount(DeviceOption option)
    {
        ArgumentNullException.ThrowIfNull(option);
        return Provider(option.ProviderName).OpenMount(option.DeviceId);
    }

    public ICamera OpenCamera(string providerName, string deviceId) =>
        Provider(providerName).OpenCamera(deviceId);

    public IMount OpenMount(string providerName, string deviceId) =>
        Provider(providerName).OpenMount(deviceId);

    /// <summary>
    /// Finds a previously-stored selection again, or null if the plugin set has
    /// changed since. Returning null rather than throwing is the point: a device
    /// that is simply not plugged in tonight should leave the picker empty, not
    /// stop the application starting.
    /// </summary>
    public DeviceOption? Find(DeviceRole kind, string? providerName, string? deviceId)
    {
        if (string.IsNullOrEmpty(providerName) || string.IsNullOrEmpty(deviceId))
        {
            return null;
        }

        IReadOnlyList<DeviceOption> options = kind == DeviceRole.Camera ? Cameras : Mounts;
        return options.FirstOrDefault(o =>
            string.Equals(o.ProviderName, providerName, StringComparison.Ordinal) &&
            string.Equals(o.DeviceId, deviceId, StringComparison.Ordinal));
    }

    private IDeviceProvider Provider(string providerName) =>
        _providers.TryGetValue(providerName, out IDeviceProvider? provider)
            ? provider
            : throw new ArgumentException($"No device provider named '{providerName}' is loaded.", nameof(providerName));

    private static void Collect(
        IDeviceProvider provider,
        DeviceRole kind,
        List<DeviceOption> into,
        List<string> problems)
    {
        try
        {
            IReadOnlyList<DeviceDescriptor> discovered = kind == DeviceRole.Camera
                ? provider.DiscoverCameras()
                : provider.DiscoverMounts();

            foreach (DeviceDescriptor descriptor in discovered)
            {
                into.Add(new DeviceOption(
                    kind, provider.Name, descriptor.Id, descriptor.DisplayName, descriptor.DriverInfo));
            }
        }
        catch (Exception ex)
        {
            // A provider that throws during discovery -- the missing-ASCOM-Platform
            // case D4 names -- must cost only its own devices, not everyone's.
            problems.Add($"Provider '{provider.Name}' could not list its {kind.ToString().ToLowerInvariant()}s: {ex.Message}");
        }
    }
}
