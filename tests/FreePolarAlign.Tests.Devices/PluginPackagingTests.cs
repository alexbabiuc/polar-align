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

    private const string PluginAssemblyName = "FreePolarAlign.Devices.Ascom";

    /// <summary>
    /// The ASCOM plugin's build output for the configuration these tests were
    /// built in.
    ///
    /// Found by walking up to the repository root and back down, and pinned to
    /// the same configuration as the running test assembly: a stale output from
    /// the *other* configuration is not what this test run built, and failing on
    /// it would be a false alarm.
    /// </summary>
    private static string PluginOutputDirectory()
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
            directory!.FullName, "src", PluginAssemblyName, "bin", configuration, "net10.0-windows");

        Assert.True(
            File.Exists(Path.Combine(output, PluginAssemblyName + ".dll")),
            $"expected the plugin's {configuration} build output at '{output}'");

        return output;
    }

    /// <summary>
    /// Nothing the host also has may sit beside the plugin. Enumerated as the
    /// files actually present rather than checked one at a time, so the failure
    /// message says what is there.
    /// </summary>
    [Fact]
    public void ThePluginDoesNotShipTheAssembliesItSharesWithTheHost()
    {
        string output = PluginOutputDirectory();

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
    [Fact]
    public void ThePluginsDependencyManifestDoesNotNameThemEither()
    {
        string manifest = Path.Combine(PluginOutputDirectory(), PluginAssemblyName + ".deps.json");
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
    [Fact]
    public void ThePluginShipsItsOwnAssembly() =>
        Assert.True(File.Exists(Path.Combine(PluginOutputDirectory(), PluginAssemblyName + ".dll")));
}
