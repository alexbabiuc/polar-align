using FreePolarAlign.Devices.Plugins;
using Xunit;

namespace FreePolarAlign.Tests.Devices;

/// <summary>
/// Genuine coverage of <see cref="PluginLoader"/> against real assemblies on
/// disk, compiled on the fly by <see cref="FixtureAssemblyCompiler"/> -- no
/// mocking of the loader's own file-system or reflection calls, since that
/// would only prove the mocks behave as configured.
/// </summary>
public sealed class PluginLoaderTests : IDisposable
{
    private readonly string _root;

    public PluginLoaderTests()
    {
        _root = Directory.CreateTempSubdirectory("fpa-plugin-tests-").FullName;
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
            // Best-effort cleanup; a locked file on some platform must not fail the test run.
        }
    }

    [Fact]
    public void MissingDirectory_ReturnsFailureNotEmptySilence()
    {
        string missing = Path.Combine(_root, "does-not-exist");

        PluginLoadResult result = PluginLoader.Load(missing);

        Assert.Empty(result.Providers);
        Assert.Single(result.Failures);
        Assert.Contains(missing, result.Failures[0].Source);
        Assert.Contains("does not exist", result.Failures[0].Message);
    }

    [Fact]
    public void EmptyDirectory_ReturnsEmptyProvidersNoFailures()
    {
        PluginLoadResult result = PluginLoader.Load(_root);

        Assert.Empty(result.Providers);
        Assert.Empty(result.Failures);
    }

    [Fact]
    public void DirectoryOfNonAssemblyFiles_ProducesReadableFailure()
    {
        string pluginDir = Path.Combine(_root, "notes-only");
        Directory.CreateDirectory(pluginDir);
        File.WriteAllText(Path.Combine(pluginDir, "readme.txt"), "not a plugin");

        PluginLoadResult result = PluginLoader.Load(_root);

        Assert.Empty(result.Providers);
        PluginLoadFailure failure = Assert.Single(result.Failures);
        Assert.Contains("no .dll files", failure.Message);
    }

    [Fact]
    public void GarbageDll_ProducesReadableLoadFailure_NotAnUnhandledException()
    {
        string pluginDir = Path.Combine(_root, "corrupt");
        FixtureAssemblyCompiler.WriteGarbageDll(pluginDir, "Corrupt.dll");

        PluginLoadResult result = PluginLoader.Load(_root);

        Assert.Empty(result.Providers);
        PluginLoadFailure failure = Assert.Single(result.Failures);
        Assert.Contains("could not be loaded", failure.Message);
    }

    [Fact]
    public void AssemblyWithNoProviders_ProducesReadableFailure()
    {
        string pluginDir = Path.Combine(_root, "no-providers");
        FixtureAssemblyCompiler.CompileToFile(
            """
            namespace NoProvidersFixture
            {
                public sealed class SomeUnrelatedClass
                {
                    public int Value => 42;
                }
            }
            """,
            pluginDir,
            "NoProvidersFixture");

        PluginLoadResult result = PluginLoader.Load(_root);

        Assert.Empty(result.Providers);
        PluginLoadFailure failure = Assert.Single(result.Failures);
        Assert.Contains("does not contain any usable", failure.Message);
    }

    [Fact]
    public void ProviderThatThrowsFromConstructor_IsReportedAsFailure_OtherProvidersStillLoad()
    {
        string pluginDir = Path.Combine(_root, "mixed");
        FixtureAssemblyCompiler.CompileToFile(WorkingProviderSource("GoodProvider"), pluginDir, "GoodProviderAssembly");
        FixtureAssemblyCompiler.CompileToFile(ThrowingProviderSource("BadProvider"), pluginDir, "BadProviderAssembly");

        PluginLoadResult result = PluginLoader.Load(_root);

        FreePolarAlign.Devices.IDeviceProvider provider = Assert.Single(result.Providers);
        Assert.Equal("GoodProvider", provider.Name);

        PluginLoadFailure failure = Assert.Single(result.Failures);
        Assert.Contains("BadProvider", failure.Message);
        Assert.Contains("boom", failure.Message);
    }

    [Fact]
    public void ValidProviderInSubdirectory_IsDiscoveredAndInstantiated()
    {
        string pluginDir = Path.Combine(_root, "good-plugin");
        FixtureAssemblyCompiler.CompileToFile(WorkingProviderSource("SubdirectoryProvider"), pluginDir, "SubdirectoryProviderAssembly");

        PluginLoadResult result = PluginLoader.Load(_root);

        FreePolarAlign.Devices.IDeviceProvider provider = Assert.Single(result.Providers);
        Assert.Equal("SubdirectoryProvider", provider.Name);
        Assert.Empty(result.Failures);
    }

    /// <summary>
    /// A native library beside a plugin is its dependency, not a failed plugin.
    /// Reported as one, ZWO's ASICamera2.dll -- shipped in the plugin's own
    /// folder (D25) -- put a warning on screen at every start. It also sorts
    /// ahead of the plugin's assembly, which must still be what the load context
    /// is rooted at. The real DLL from resources/ is used because "native" is
    /// exactly the property under test, and a fabricated PE could get it wrong.
    /// </summary>
    [Fact]
    public void ANativeLibraryBesideAPlugin_IsNotReportedAsAFailure()
    {
        string pluginDir = Path.Combine(_root, "zwo-like");
        FixtureAssemblyCompiler.CompileToFile(WorkingProviderSource("NativeNeighbourProvider"), pluginDir, "NativeNeighbourAssembly");
        File.Copy(ZwoNativeLibrary, Path.Combine(pluginDir, "ASICamera2.dll"));

        PluginLoadResult result = PluginLoader.Load(_root);

        Assert.Equal("NativeNeighbourProvider", Assert.Single(result.Providers).Name);
        Assert.Empty(result.Failures);
    }

    /// <summary>A folder of native libraries alone is still not a plugin, and says so.</summary>
    [Fact]
    public void APluginFolderOfOnlyNativeLibraries_SaysSo()
    {
        string pluginDir = Path.Combine(_root, "native-only");
        Directory.CreateDirectory(pluginDir);
        File.Copy(ZwoNativeLibrary, Path.Combine(pluginDir, "ASICamera2.dll"));

        PluginLoadResult result = PluginLoader.Load(_root);

        Assert.Empty(result.Providers);
        Assert.Contains("no .NET assembly", Assert.Single(result.Failures).Message);
    }

    private static string ZwoNativeLibrary
    {
        get
        {
            DirectoryInfo? directory = new(AppContext.BaseDirectory);
            while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "FreePolarAlign.sln")))
            {
                directory = directory.Parent;
            }

            Assert.NotNull(directory);
            return Path.Combine(directory!.FullName, "resources", "zwo", "camera", "libs", "x64", "ASICamera2.dll");
        }
    }

    [Fact]
    public void ValidProviderAsLooseDllInRoot_IsDiscoveredAndInstantiated()
    {
        FixtureAssemblyCompiler.CompileToFile(WorkingProviderSource("LooseFileProvider"), _root, "LooseFileProviderAssembly");

        PluginLoadResult result = PluginLoader.Load(_root);

        FreePolarAlign.Devices.IDeviceProvider provider = Assert.Single(result.Providers);
        Assert.Equal("LooseFileProvider", provider.Name);
        Assert.Empty(result.Failures);
    }

    [Fact]
    public void MultiplePluginDirectories_EachIsolated_AllProvidersLoad()
    {
        FixtureAssemblyCompiler.CompileToFile(WorkingProviderSource("PluginA"), Path.Combine(_root, "plugin-a"), "PluginAAssembly");
        FixtureAssemblyCompiler.CompileToFile(WorkingProviderSource("PluginB"), Path.Combine(_root, "plugin-b"), "PluginBAssembly");

        PluginLoadResult result = PluginLoader.Load(_root);

        Assert.Empty(result.Failures);
        Assert.Equal(["PluginA", "PluginB"], result.Providers.Select(p => p.Name).OrderBy(n => n));
    }

    [Fact]
    public void LoadedProvider_SatisfiesHostSideIDeviceProviderTypeIdentity()
    {
        // The regression this guards against: if the plugin's own copy of
        // FreePolarAlign.Devices.dll were loaded into the plugin's isolated
        // AssemblyLoadContext instead of falling back to the default context,
        // `provider is IDeviceProvider` would fail here even though the class
        // genuinely implements the interface, because the two ALCs would each
        // have their own distinct IDeviceProvider type.
        string pluginDir = Path.Combine(_root, "identity-check");
        FixtureAssemblyCompiler.CompileToFile(WorkingProviderSource("IdentityCheckProvider"), pluginDir, "IdentityCheckAssembly");

        PluginLoadResult result = PluginLoader.Load(_root);

        object provider = Assert.Single(result.Providers);
        Assert.IsAssignableFrom<FreePolarAlign.Devices.IDeviceProvider>(provider);
    }

    private static string WorkingProviderSource(string providerName) => $$"""
        using System.Collections.Generic;
        using FreePolarAlign.Devices;

        namespace PluginFixtures
        {
            public sealed class {{providerName}} : IDeviceProvider
            {
                public string Name => "{{providerName}}";
                public string Version => "1.0.0";
                public IReadOnlyList<DeviceDescriptor> DiscoverDevices() => new List<DeviceDescriptor>();
                public ICamera OpenCamera(string deviceId) => throw new System.NotSupportedException();
                public IMount OpenMount(string deviceId) => throw new System.NotSupportedException();
            }
        }
        """;

    private static string ThrowingProviderSource(string providerName) => $$"""
        using System.Collections.Generic;
        using FreePolarAlign.Devices;

        namespace PluginFixtures
        {
            public sealed class {{providerName}} : IDeviceProvider
            {
                public {{providerName}}()
                {
                    throw new System.InvalidOperationException("boom");
                }

                public string Name => "{{providerName}}";
                public string Version => "1.0.0";
                public IReadOnlyList<DeviceDescriptor> DiscoverDevices() => new List<DeviceDescriptor>();
                public ICamera OpenCamera(string deviceId) => throw new System.NotSupportedException();
                public IMount OpenMount(string deviceId) => throw new System.NotSupportedException();
            }
        }
        """;
}
