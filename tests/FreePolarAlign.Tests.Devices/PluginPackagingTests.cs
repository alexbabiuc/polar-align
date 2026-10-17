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

    private static void SkipUnlessToupTekLibraryIsInResources(string runtimeIdentifier)
    {
        string architecture = runtimeIdentifier == "win-x86" ? "x86" : "x64";
        string source = Path.Combine(RepositoryRoot(), "resources", "touptek", "camera", architecture, "toupcam.dll");
        Skip.IfNot(File.Exists(source), $"ToupTek's library is not in this checkout ('{source}'); it is kept out of source control (D25).");
    }

    private static string RepositoryRoot()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "FreePolarAlign.sln")))
        {
            directory = directory.Parent;
        }

        Assert.NotNull(directory);
        return directory!.FullName;
    }

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

    /// <summary>
    /// The ToupTek plugin ships ToupTek's library, from resources/touptek, the
    /// same way and checked the same way as ZWO's.
    /// </summary>
    /// <remarks>
    /// Skipped, not failed, on a checkout without the library: it is kept out of
    /// source control (D25), so a fresh clone has nothing to ship. The skip asks
    /// whether the library is in resources/, not in the output, so a library
    /// that is present and still not copied fails as before.
    /// </remarks>
    [SkippableTheory]
    [InlineData("win-x64", (ushort)0x8664)]
    [InlineData("win-x86", (ushort)0x014C)]
    public void TheToupTekPluginShipsEachArchitecturesLibrary_InItsOwnFolder(string runtimeIdentifier, ushort machine)
    {
        SkipUnlessToupTekLibraryIsInResources(runtimeIdentifier);

        string library = Path.Combine(
            PluginOutputDirectory("FreePolarAlign.Devices.ToupTek", "net10.0"),
            "runtimes", runtimeIdentifier, "native", "toupcam.dll");

        Assert.True(File.Exists(library), $"expected ToupTek's library at '{library}'");
        Assert.Equal(machine, PeMachine(library));
    }

    public static TheoryData<string, string, string> ShippedLibraries => new()
    {
        { "FreePolarAlign.Devices.Zwo", "ASICamera2.dll", "win-x64" },
        { "FreePolarAlign.Devices.Zwo", "ASICamera2.dll", "win-x86" },
        { "FreePolarAlign.Devices.ToupTek", "toupcam.dll", "win-x64" },
        { "FreePolarAlign.Devices.ToupTek", "toupcam.dll", "win-x86" },
    };

    /// <summary>
    /// Every function the plugin declares is exported by the library it ships
    /// with. A declaration the library lacks compiles, loads, and fails only when
    /// first called -- with a camera attached, at night -- as an
    /// EntryPointNotFoundException. Checked against the export table, for both
    /// architectures, so an SDK update that renames or drops a function is
    /// caught here, and so is a declaration typed wrong.
    /// </summary>
    [SkippableTheory]
    [MemberData(nameof(ShippedLibraries))]
    public void EveryDeclaredFunction_IsExportedByTheShippedLibrary(string plugin, string libraryFile, string runtimeIdentifier)
    {
        if (plugin.EndsWith("ToupTek", StringComparison.Ordinal))
        {
            SkipUnlessToupTekLibraryIsInResources(runtimeIdentifier);
        }

        string library = Path.Combine(PluginOutputDirectory(plugin, "net10.0"), "runtimes", runtimeIdentifier, "native", libraryFile);
        Type bindings = plugin.EndsWith("ToupTek", StringComparison.Ordinal)
            ? typeof(FreePolarAlign.Devices.ToupTek.ToupTekDeviceProvider).Assembly.GetType("FreePolarAlign.Devices.ToupTek.ToupcamNative", throwOnError: true)!
            : typeof(FreePolarAlign.Devices.Zwo.ZwoDeviceProvider).Assembly.GetType("FreePolarAlign.Devices.Zwo.AsiNative", throwOnError: true)!;

        string[] declared = bindings
            .GetMethods(System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic)
            .Select(method => method.GetCustomAttributes(typeof(System.Runtime.InteropServices.DllImportAttribute), false)
                .Cast<System.Runtime.InteropServices.DllImportAttribute>()
                .Select(import => import.EntryPoint ?? method.Name)
                .FirstOrDefault())
            .OfType<string>()
            .Distinct()
            .ToArray();

        IReadOnlySet<string> exported = PeExports(library);

        string[] missing = declared.Where(name => !exported.Contains(name)).ToArray();

        Assert.NotEmpty(declared);
        Assert.True(missing.Length == 0, $"'{library}' does not export: {string.Join(", ", missing)}");
    }

    /// <summary>The names in a PE image's export table.</summary>
    private static IReadOnlySet<string> PeExports(string path)
    {
        byte[] image = File.ReadAllBytes(path);
        int pe = BitConverter.ToInt32(image, 0x3C);
        int sectionCount = BitConverter.ToUInt16(image, pe + 6);
        int optionalSize = BitConverter.ToUInt16(image, pe + 20);
        int optional = pe + 24;

        // The data directories start 96 bytes into a PE32 optional header and
        // 112 into a PE32+ one; the export table is the first of them.
        bool pe32Plus = BitConverter.ToUInt16(image, optional) == 0x20B;
        int exportRva = BitConverter.ToInt32(image, optional + (pe32Plus ? 112 : 96));

        int sections = optional + optionalSize;
        int Offset(int rva)
        {
            for (int i = 0; i < sectionCount; i++)
            {
                int header = sections + (i * 40);
                int virtualAddress = BitConverter.ToInt32(image, header + 12);
                int size = Math.Max(BitConverter.ToInt32(image, header + 8), BitConverter.ToInt32(image, header + 16));
                if (rva >= virtualAddress && rva < virtualAddress + size)
                {
                    return rva - virtualAddress + BitConverter.ToInt32(image, header + 20);
                }
            }

            throw new InvalidDataException($"RVA 0x{rva:x} is in no section of '{path}'.");
        }

        int directory = Offset(exportRva);
        int nameCount = BitConverter.ToInt32(image, directory + 24);
        int names = Offset(BitConverter.ToInt32(image, directory + 32));

        var exported = new HashSet<string>(StringComparer.Ordinal);
        for (int i = 0; i < nameCount; i++)
        {
            int start = Offset(BitConverter.ToInt32(image, names + (i * 4)));
            int end = Array.IndexOf(image, (byte)0, start);
            exported.Add(System.Text.Encoding.ASCII.GetString(image, start, end - start));
        }

        return exported;
    }

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
