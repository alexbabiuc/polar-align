using System.Text.Json;
using Xunit;

namespace FreePolarAlign.Tests.Devices;

/// <summary>
/// What a plugin is allowed to have sitting next to it on disk.
///
/// A plugin loads in an isolated context (D4), and
/// <c>PluginAssemblyLoadContext</c> resolves an assembly from the plugin's own
/// directory before falling back to the host. So a copy of a shared assembly
/// beside the plugin is not a harmless duplicate file: it is loaded as a
/// *second* assembly identity, with the host's copy still loaded alongside it.
/// Measured against a folder shaped like the published one, the plugin's
/// FreePolarAlign.Imaging and the host's came back as different assemblies;
/// with the copies removed, the resolver found nothing, the side-by-side probe
/// found nothing, and the host's was used.
///
/// The load context already hard-codes an exemption for the contract assembly,
/// FreePolarAlign.Devices, because a duplicate of *that* would make every
/// provider fail an <c>is IDeviceProvider</c> test against a type that merely
/// looks identical. Nothing exempts Core or Imaging, so they must not ship.
///
/// The version skew is the worse half in practice: a plugin folder republished
/// one build later than the host keeps an older Core and Imaging and runs
/// against those, while the session log reports only the host's stamp. The
/// binary being diagnosed would not be the binary running, which is the exact
/// failure the project's versioning rule exists to prevent.
///
/// This is checked against the build output rather than asserted about the
/// project file, because the project file is the cause and the output is the
/// thing that ships. A future SDK that stopped honouring the setting would keep
/// the project file looking right.
/// </summary>
public class PluginPackagingTests
{
    /// <summary>
    /// Assemblies the host always has. A plugin compiles against them and must
    /// never carry its own copy.
    /// </summary>
    private static readonly string[] SharedWithTheHost =
    {
        "FreePolarAlign.Devices",
        "FreePolarAlign.Core",
        "FreePolarAlign.Imaging",
    };

    /// <summary>Every plugin the solution builds, with the framework it targets.</summary>
    public static TheoryData<string, string> Plugins => new()
    {
        { "FreePolarAlign.Devices.Ascom", "net10.0-windows" },
        { "FreePolarAlign.Devices.Zwo", "net10.0" },
        { "FreePolarAlign.Devices.ToupTek", "net10.0" },
    };

    /// <summary>
    /// The ASCOM plugin's build output for the configuration these tests were
    /// built in.
    ///
    /// Found by walking up to the repository root and back down, and pinned to
    /// the same configuration as the running test assembly: a stale output from
    /// the *other* configuration is not what this test run built, and failing on
    /// it would be a false alarm.
    /// </summary>
    private static string PluginOutputDirectory(string pluginAssemblyName, string targetFramework)
    {
        string here = AppContext.BaseDirectory;

        string configuration = here.Contains($"{Path.DirectorySeparatorChar}Release{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
            ? "Release"
            : "Debug";

        DirectoryInfo? directory = new(here);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "FreePolarAlign.sln")))
        {
            directory = directory.Parent;
        }

        Assert.NotNull(directory);

        string output = Path.Combine(
            directory!.FullName, "src", pluginAssemblyName, "bin", configuration, targetFramework);

        Assert.True(
            File.Exists(Path.Combine(output, pluginAssemblyName + ".dll")),
            $"expected the plugin's {configuration} build output at '{output}'");

        return output;
    }

    /// <summary>
    /// Nothing the host also has may sit beside the plugin. Enumerated as the
    /// files actually present rather than checked one at a time, so the failure
    /// message says what is there.
    /// </summary>
    /// <remarks>
    /// Covers every plugin rather than the one that first had the problem. The
    /// ZWO plugin's first build carried FreePolarAlign.Imaging, which it never
    /// names -- it arrived transitively through FreePolarAlign.Devices -- and the
    /// Ascom-only version of this test would not have seen it.
    /// </remarks>
    [Theory]
    [MemberData(nameof(Plugins))]
    public void ThePluginDoesNotShipTheAssembliesItSharesWithTheHost(string plugin, string targetFramework)
    {
        string output = PluginOutputDirectory(plugin, targetFramework);

        string[] offenders = SharedWithTheHost
            .Select(name => Path.Combine(output, name + ".dll"))
            .Where(File.Exists)
            .Select(Path.GetFileName)
            .ToArray()!;

        Assert.True(
            offenders.Length == 0,
            $"the plugin build output carries the host's own assemblies: {string.Join(", ", offenders)}. " +
            "Each would load as a second assembly identity inside the plugin's load context.");
    }

    /// <summary>
    /// And the manifest does not name them either.
    ///
    /// Deleting the files alone would not be enough: the load context resolves
    /// through an <c>AssemblyDependencyResolver</c> built from this file, so a
    /// runtime entry here is an instruction to go and find a copy. The two
    /// halves are what <c>ExcludeAssets="runtime"</c> removes together, and a
    /// change that removed only the files would pass the test above.
    /// </summary>
    [Theory]
    [MemberData(nameof(Plugins))]
    public void ThePluginsDependencyManifestDoesNotNameThemEither(string plugin, string targetFramework)
    {
        string manifest = Path.Combine(PluginOutputDirectory(plugin, targetFramework), plugin + ".deps.json");
        Assert.True(File.Exists(manifest), $"expected a dependency manifest at '{manifest}'");

        using JsonDocument document = JsonDocument.Parse(File.ReadAllText(manifest));

        var named = new List<string>();
        foreach (JsonProperty target in document.RootElement.GetProperty("targets").EnumerateObject())
        {
            foreach (JsonProperty library in target.Value.EnumerateObject())
            {
                if (!library.Value.TryGetProperty("runtime", out JsonElement runtime))
                {
                    continue;
                }

                named.AddRange(runtime
                    .EnumerateObject()
                    .Select(file => Path.GetFileNameWithoutExtension(file.Name))
                    .Where(name => SharedWithTheHost.Contains(name, StringComparer.OrdinalIgnoreCase)));
            }
        }

        Assert.True(
            named.Count == 0,
            $"the plugin's deps.json lists the host's own assemblies as runtime dependencies: {string.Join(", ", named.Distinct())}.");
    }

    /// <summary>
    /// The plugin's own assembly is of course still there. Without this, the
    /// two tests above would pass just as happily against an empty directory.
    /// </summary>
    [Theory]
    [MemberData(nameof(Plugins))]
    public void ThePluginShipsItsOwnAssembly(string plugin, string targetFramework) =>
        Assert.True(File.Exists(Path.Combine(PluginOutputDirectory(plugin, targetFramework), plugin + ".dll")));

    // ---- Vendor libraries shipped with a plugin ----

    /// <summary>
    /// The ZWO plugin ships ZWO's own library, from resources/zwo, under the
    /// architecture folder it belongs to -- in a build with no runtime
    /// identifier, both, in the runtimes/&lt;rid&gt;/native layout the plugin's
    /// library locator probes.
    ///
    /// Checked by reading each DLL's PE header rather than trusting its folder:
    /// an x86 and an x64 build swapped in resources/ would pass every build and
    /// every other test, and fail only at the telescope, as a camera that never
    /// appears. The release publish (win-x64) puts just the matching DLL beside
    /// the plugin; that path is plain MSBuild conditions on the runtime
    /// identifier and was verified by publishing for both architectures.
    /// </summary>
    [Theory]
    [InlineData("win-x64", (ushort)0x8664)]
    [InlineData("win-x86", (ushort)0x014C)]
    public void TheZwoPluginShipsEachArchitecturesLibrary_InItsOwnFolder(string runtimeIdentifier, ushort machine)
    {
        string library = Path.Combine(
            PluginOutputDirectory("FreePolarAlign.Devices.Zwo", "net10.0"),
            "runtimes", runtimeIdentifier, "native", "ASICamera2.dll");

        Assert.True(File.Exists(library), $"expected ZWO's library at '{library}'");
        Assert.Equal(machine, PeMachine(library));
    }

    /// <summary>
    /// ZWO's licence permits redistribution only with its notice included, so
    /// wherever the library goes the notice goes too.
    /// </summary>
    [Fact]
    public void TheZwoLibraryTravelsWithItsLicence()
    {
        string notice = Path.Combine(
            PluginOutputDirectory("FreePolarAlign.Devices.Zwo", "net10.0"), "ASICamera2.license.txt");

        Assert.True(File.Exists(notice), $"expected ZWO's licence notice at '{notice}'");
        Assert.Contains("ZWO Company", File.ReadAllText(notice), StringComparison.Ordinal);
    }

    /// <summary>
    /// The import libraries beside the DLLs in resources/ are for C code linking
    /// at build time. The plugin binds through P/Invoke and never needs them, so
    /// they stay out of the output.
    /// </summary>
    [Fact]
    public void TheZwoImportLibrariesAreNotShipped() =>
        Assert.Empty(Directory.EnumerateFiles(
            PluginOutputDirectory("FreePolarAlign.Devices.Zwo", "net10.0"), "*.lib", SearchOption.AllDirectories));

    /// <summary>The COFF machine field of a PE image: 0x8664 for x64, 0x014C for x86.</summary>
    private static ushort PeMachine(string path)
    {
        using var reader = new BinaryReader(File.OpenRead(path));
        reader.BaseStream.Seek(0x3C, SeekOrigin.Begin);
        int peHeader = reader.ReadInt32();
        reader.BaseStream.Seek(peHeader, SeekOrigin.Begin);
        Assert.Equal(0x00004550u, reader.ReadUInt32()); // "PE\0\0"
        return reader.ReadUInt16();
    }
}
