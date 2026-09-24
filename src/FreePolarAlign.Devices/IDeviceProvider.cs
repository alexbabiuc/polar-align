namespace FreePolarAlign.Devices;

/// <summary>Whether a discovered device is a camera or a mount, which decides the list it appears in and how it is opened.</summary>
public enum DeviceRole
{
    Camera,
    Mount
}

/// <summary>
/// One device a provider can open, discovered before connection: a
/// <see cref="CameraDescriptor"/> or a <see cref="MountDescriptor"/>. Mostly
/// identification; the full capability picture lives on the opened
/// <see cref="ICamera"/>/<see cref="IMount"/>.
///
/// A small hierarchy rather than one record with a role field, because the
/// capabilities worth knowing at discovery are not shared. Gain control means
/// nothing for a mount, and a single record would let a provider say a mount
/// had it -- a state the type allowed and nothing could read sensibly. Each
/// role-specific capability lives on its own subtype; what every device can
/// have lives here.
///
/// The constructor is <c>private protected</c>, so only this assembly can add a
/// kind of device. Not perfectly closed: a record that is not sealed must have a
/// <c>protected</c> copy constructor, and a plugin could derive through that by
/// wrapping one of these. The catalogue therefore switches on the known
/// subtypes and reports anything else as a problem, rather than trusting
/// <see cref="Role"/>.
/// </summary>
public abstract record DeviceDescriptor
{
    private protected DeviceDescriptor(string id, string displayName, string driverInfo, bool hasSetupDialog)
    {
        Id = id;
        DisplayName = displayName;
        DriverInfo = driverInfo;
        HasSetupDialog = hasSetupDialog;
    }

    public string Id { get; }

    public string DisplayName { get; }

    public string DriverInfo { get; }

    /// <summary>
    /// The driver has a settings window of its own that can be opened *before*
    /// connecting. A property of the kind of driver, not of the device: every
    /// ASCOM driver has one, cameras and telescopes alike, and no native SDK
    /// does. Known at discovery so the UI can offer the right control as soon as
    /// a device is picked.
    /// </summary>
    public bool HasSetupDialog { get; }

    /// <summary>Which list the device appears in. Derived from the subtype, never stored.</summary>
    public abstract DeviceRole Role { get; }
}

/// <param name="HasGainControl">
/// The provider sets gain itself, so the UI should offer a gain control for
/// this camera. The range is only known once the camera is open; this says only
/// that there will be one. Cameras only, which is why it is here and not on
/// <see cref="DeviceDescriptor"/>.
/// </param>
public sealed record CameraDescriptor(
    string Id,
    string DisplayName,
    string DriverInfo,
    bool HasSetupDialog = false,
    bool HasGainControl = false)
    : DeviceDescriptor(Id, DisplayName, DriverInfo, HasSetupDialog)
{
    public override DeviceRole Role => DeviceRole.Camera;
}

public sealed record MountDescriptor(
    string Id,
    string DisplayName,
    string DriverInfo,
    bool HasSetupDialog = false)
    : DeviceDescriptor(Id, DisplayName, DriverInfo, HasSetupDialog)
{
    public override DeviceRole Role => DeviceRole.Mount;
}

/// <summary>
/// Thrown from discovery when a provider's prerequisites are simply absent --
/// a vendor library that is not installed -- as opposed to present and broken.
///
/// The distinction decides whether the user sees a warning. Someone who owns no
/// ZWO camera has no ZWO library, and a permanent banner saying so would train
/// them to ignore the banner, which is also where a genuinely broken plugin is
/// reported. So this is recorded in the log and nowhere louder. A library that
/// is present but the wrong architecture, or too old to have a function this
/// plugin needs, is a real fault and must not use this type.
/// </summary>
public sealed class ProviderUnavailableException : Exception
{
    public ProviderUnavailableException(string message)
        : base(message)
    {
    }

    public ProviderUnavailableException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

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

    /// <summary>
    /// Every device this provider can open, cameras and mounts together, as
    /// <see cref="CameraDescriptor"/>s and <see cref="MountDescriptor"/>s.
    ///
    /// One call, so one failure costs the provider its whole list rather than
    /// only the cameras or only the mounts. That granularity used to exist and
    /// bought nothing in practice: the ASCOM provider's two lists fail together
    /// when the Platform is missing, and every other provider lists one kind.
    /// A provider that can lose one kind independently of the other should
    /// catch that itself and return what it has.
    /// </summary>
    IReadOnlyList<DeviceDescriptor> DiscoverDevices();

    /// <summary>
    /// Opening stays split by role, unlike discovery, because the two return
    /// different things and a merged method would push a cast onto every caller.
    /// </summary>

    ICamera OpenCamera(string deviceId);

    IMount OpenMount(string deviceId);
}
