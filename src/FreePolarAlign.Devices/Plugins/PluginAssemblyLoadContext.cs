using System.Reflection;
using System.Runtime.Loader;

namespace FreePolarAlign.Devices.Plugins;

/// <summary>
/// One isolated load context per plugin (D4: "providers load in isolation and
/// a bad plugin cannot take down the host"). Collectible, so a plugin's
/// assemblies can in principle be unloaded again.
///
/// The one identity that must NOT be duplicated across contexts is the
/// contract assembly itself (<see cref="IDeviceProvider"/> and friends): if a
/// plugin's build output carries its own copy of FreePolarAlign.Devices.dll
/// and this context loaded it, the loaded type would satisfy
/// <c>is IDeviceProvider</c> against a *different* <c>IDeviceProvider</c> type
/// than the host's, and every provider from that plugin would silently fail
/// to match. So the contract assembly is deliberately never resolved here;
/// returning null from <see cref="Load"/> falls back to the default load
/// context, which is where the host (and therefore the one true contract
/// assembly) already lives.
/// </summary>
internal sealed class PluginAssemblyLoadContext : AssemblyLoadContext
{
    private static readonly string ContractAssemblyName = typeof(IDeviceProvider).Assembly.GetName().Name!;

    private readonly AssemblyDependencyResolver _resolver;
    private readonly string _pluginDirectory;

    public PluginAssemblyLoadContext(string name, string mainAssemblyPath)
        : base(name, isCollectible: true)
    {
        _resolver = new AssemblyDependencyResolver(mainAssemblyPath);
        _pluginDirectory = Path.GetDirectoryName(mainAssemblyPath) ?? ".";
    }

    protected override Assembly? Load(AssemblyName assemblyName)
    {
        if (string.Equals(assemblyName.Name, ContractAssemblyName, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        string? resolvedPath = _resolver.ResolveAssemblyToPath(assemblyName);
        if (resolvedPath != null)
        {
            return LoadFromAssemblyPath(resolvedPath);
        }

        string sideBySideCandidate = Path.Combine(_pluginDirectory, assemblyName.Name + ".dll");
        return File.Exists(sideBySideCandidate) ? LoadFromAssemblyPath(sideBySideCandidate) : null;
    }

    protected override nint LoadUnmanagedDll(string unmanagedDllName)
    {
        string? resolvedPath = _resolver.ResolveUnmanagedDllToPath(unmanagedDllName);
        return resolvedPath != null ? LoadUnmanagedDllFromPath(resolvedPath) : nint.Zero;
    }
}
