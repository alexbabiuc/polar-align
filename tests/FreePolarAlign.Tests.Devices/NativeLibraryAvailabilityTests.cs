using FreePolarAlign.Devices;
using FreePolarAlign.Devices.Plugins;
using Xunit;

namespace FreePolarAlign.Tests.Devices;

/// <summary>
/// Absent versus broken, for a vendor library a plugin needs.
///
/// The runtime reports both as the same <see cref="DllNotFoundException"/>, and
/// they need opposite treatment. Absent is the normal state for anyone who does
/// not own that brand of camera and must not raise a warning, or the warning
/// banner becomes something people learn to ignore. Broken -- a 32-bit DLL
/// dropped beside a 64-bit build, say -- is exactly what the banner is for.
/// </summary>
public class NativeLibraryAvailabilityTests
{
    private static NativeLibraryLocator LocatorFor(string stem) => new(
        typeof(NativeLibraryAvailabilityTests).Assembly,
        stem,
        "Test Vendor",
        windowsFile: stem + ".dll",
        macFile: "lib" + stem + ".dylib",
        linuxFile: "lib" + stem + ".so");

    [Fact]
    public void AnAbsentLibraryIsUnavailable_AndSaysWhereItLooked()
    {
        NativeLibraryLocator locator = LocatorFor("fpa_no_such_library_" + Guid.NewGuid().ToString("N"));

        ProviderUnavailableException error = Assert.Throws<ProviderUnavailableException>(locator.EnsureLoaded);

        Assert.Contains(locator.FileName, error.Message, StringComparison.Ordinal);
        Assert.Contains(AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar), error.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// A file that is there but will not load is a fault, and reports its own
    /// error. Before this distinction it would have been filed as "not
    /// installed" and logged quietly, leaving a user with the library in place
    /// wondering why their camera never appeared.
    /// </summary>
    [Fact]
    public void APresentButUnloadableLibraryIsAFault_NotAnAbsence()
    {
        string stem = "fpa_broken_library_" + Guid.NewGuid().ToString("N");
        NativeLibraryLocator locator = LocatorFor(stem);
        string path = Path.Combine(AppContext.BaseDirectory, locator.FileName);

        File.WriteAllText(path, "this is not a shared library");
        try
        {
            Exception error = Assert.ThrowsAny<Exception>(locator.EnsureLoaded);
            Assert.IsNotType<ProviderUnavailableException>(error);
        }
        finally
        {
            File.Delete(path);
        }
    }

    /// <summary>
    /// A library shipped in the runtimes/&lt;rid&gt;/native layout is found, under
    /// the portable identifier of the running process. Proved with an
    /// unloadable file: the locator reporting it as a fault rather than as
    /// absent shows it looked in that folder and found it.
    /// </summary>
    [Fact]
    public void ALibraryInTheRuntimesFolderForThisProcess_IsFound()
    {
        string stem = "fpa_runtimes_library_" + Guid.NewGuid().ToString("N");
        NativeLibraryLocator locator = LocatorFor(stem);
        string folder = Path.Combine(
            AppContext.BaseDirectory, "runtimes", NativeLibraryLocator.PortableRuntimeIdentifier(), "native");
        string path = Path.Combine(folder, locator.FileName);

        Directory.CreateDirectory(folder);
        File.WriteAllText(path, "this is not a shared library");
        try
        {
            Exception error = Assert.ThrowsAny<Exception>(locator.EnsureLoaded);
            Assert.IsNotType<ProviderUnavailableException>(error);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void ThePortableIdentifierNamesThisProcessesArchitecture()
    {
        string identifier = NativeLibraryLocator.PortableRuntimeIdentifier();
        string architecture = System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture
            .ToString().ToLowerInvariant();

        Assert.EndsWith("-" + architecture, identifier, StringComparison.Ordinal);
    }

    /// <summary>
    /// And the catalogue files them accordingly: an unavailable provider becomes
    /// a note for the log, a failing one a problem for the banner.
    /// </summary>
    [Fact]
    public void TheCatalogueLogsAnUnavailableProvider_AndWarnsAboutAFailingOne()
    {
        DeviceCatalog catalog = DeviceCatalog.Create(new IDeviceProvider[]
        {
            new ThrowingProvider("Absent", new ProviderUnavailableException("its library is not installed")),
            new ThrowingProvider("Broken", new BadImageFormatException("wrong architecture")),
        });

        Assert.Contains(catalog.Notes, note => note.Contains("Absent", StringComparison.Ordinal));
        Assert.DoesNotContain(catalog.Problems, problem => problem.Contains("Absent", StringComparison.Ordinal));
        Assert.Contains(catalog.Problems, problem => problem.Contains("Broken", StringComparison.Ordinal));
    }

    /// <summary>
    /// Discovery capabilities reach the picker, since that is where they decide
    /// which camera-settings control is offered before anything connects.
    /// </summary>
    [Fact]
    public void DiscoveredCapabilitiesReachThePicker()
    {
        DeviceCatalog catalog = DeviceCatalog.Create(new IDeviceProvider[] { new ListingProvider() });

        DeviceOption option = Assert.Single(catalog.Cameras);
        Assert.True(option.HasGainControl);
        Assert.False(option.HasSetupDialog);
    }

    /// <summary>
    /// The descriptor hierarchy is closed to other assemblies except through
    /// the copy constructor records insist on -- and a descriptor derived that
    /// way can claim any role. The catalogue goes by the type, so one it does
    /// not know is left out and reported rather than put in a list it would
    /// then not know how to open.
    /// </summary>
    [Fact]
    public void ADescriptorOfAnUnknownKind_IsReported_NotListed()
    {
        DeviceCatalog catalog = DeviceCatalog.Create(new IDeviceProvider[] { new StrayProvider() });

        Assert.Empty(catalog.Cameras);
        Assert.Empty(catalog.Mounts);
        Assert.Contains(catalog.Problems, problem => problem.Contains(nameof(StrayDescriptor), StringComparison.Ordinal));
    }

    /// <summary>
    /// ASCOM telescopes have settings windows too; the flag lives on the base
    /// descriptor so a mount can say so, while gain stays camera-only.
    /// </summary>
    [Fact]
    public void AMountCanHaveASetupDialog_AndReachesThePickerWithIt()
    {
        DeviceCatalog catalog = DeviceCatalog.Create(new IDeviceProvider[] { new MountListingProvider() });

        DeviceOption mount = Assert.Single(catalog.Mounts);
        Assert.True(mount.HasSetupDialog);
        Assert.False(mount.HasGainControl);
    }

    private sealed record StrayDescriptor : DeviceDescriptor
    {
        public StrayDescriptor()
            : base(new MountDescriptor("stray", "Stray Device", "nowhere"))
        {
        }

        public override DeviceRole Role => DeviceRole.Camera;
    }

    private sealed class StrayProvider : IDeviceProvider
    {
        public string Name => "Stray";

        public string Version => "0";

        public IReadOnlyList<DeviceDescriptor> DiscoverDevices() => new DeviceDescriptor[] { new StrayDescriptor() };

        public ICamera OpenCamera(string deviceId) => throw new NotSupportedException();

        public IMount OpenMount(string deviceId) => throw new NotSupportedException();
    }

    private sealed class MountListingProvider : IDeviceProvider
    {
        public string Name => "ASCOM";

        public string Version => "0";

        public IReadOnlyList<DeviceDescriptor> DiscoverDevices() =>
            new DeviceDescriptor[] { new MountDescriptor("ASCOM.iOptron2017.Telescope", "iOptron", "ASCOM", HasSetupDialog: true) };

        public ICamera OpenCamera(string deviceId) => throw new NotSupportedException();

        public IMount OpenMount(string deviceId) => throw new NotSupportedException();
    }

    private sealed class ThrowingProvider : IDeviceProvider
    {
        private readonly Exception _error;

        public ThrowingProvider(string name, Exception error)
        {
            Name = name;
            _error = error;
        }

        public string Name { get; }

        public string Version => "0";

        public IReadOnlyList<DeviceDescriptor> DiscoverDevices() => throw _error;

        public ICamera OpenCamera(string deviceId) => throw new NotSupportedException();

        public IMount OpenMount(string deviceId) => throw new NotSupportedException();
    }

    private sealed class ListingProvider : IDeviceProvider
    {
        public string Name => "Native";

        public string Version => "0";

        public IReadOnlyList<DeviceDescriptor> DiscoverDevices() =>
            new[] { new CameraDescriptor("cam", "Camera", "SDK", HasSetupDialog: false, HasGainControl: true) };

        public ICamera OpenCamera(string deviceId) => throw new NotSupportedException();

        public IMount OpenMount(string deviceId) => throw new NotSupportedException();
    }
}
