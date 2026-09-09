using System.Reflection;

namespace FreePolarAlign.Devices.Plugins;

/// <summary>
/// D4's discovery mechanism: scan a <c>plugins/</c> directory at startup and
/// load whatever <see cref="IDeviceProvider"/> implementations are found,
/// with no compile-time reference from the host to any provider assembly.
///
/// Layout supported under the root: each immediate subdirectory is treated as
/// one plugin (its own <see cref="PluginAssemblyLoadContext"/>, so its private
/// dependencies never collide with another plugin's), and any <c>.dll</c>
/// files sitting directly in the root are each treated as a single-file
/// plugin. Every public, non-abstract, non-generic class with a public
/// parameterless constructor that implements <see cref="IDeviceProvider"/> is
/// instantiated.
///
/// Nothing here throws for an expected failure mode (missing directory,
/// non-assembly file, assembly with no providers, a provider constructor that
/// throws). Each is recorded as a <see cref="PluginLoadFailure"/> instead, so
/// one bad plugin never empties the whole result -- D4's "a missing ASCOM
/// Platform should produce a readable message, not an empty device list".
/// </summary>
public static class PluginLoader
{
    public static PluginLoadResult Load(string pluginsRootDirectory)
    {
        var providers = new List<IDeviceProvider>();
        var failures = new List<PluginLoadFailure>();

        if (!Directory.Exists(pluginsRootDirectory))
        {
            failures.Add(new PluginLoadFailure(
                pluginsRootDirectory,
                $"Plugins directory '{pluginsRootDirectory}' does not exist. No device providers beyond built-ins are available.",
                null));
            return new PluginLoadResult(providers, failures);
        }

        foreach (string subdirectory in Directory.EnumerateDirectories(pluginsRootDirectory).OrderBy(d => d, StringComparer.Ordinal))
        {
            LoadPluginDirectory(subdirectory, providers, failures);
        }

        foreach (string looseAssembly in Directory.EnumerateFiles(pluginsRootDirectory, "*.dll", SearchOption.TopDirectoryOnly)
                     .OrderBy(f => f, StringComparer.Ordinal))
        {
            LoadPluginAssembly(looseAssembly, providers, failures);
        }

        return new PluginLoadResult(providers, failures);
    }

    private static void LoadPluginDirectory(string directory, List<IDeviceProvider> providers, List<PluginLoadFailure> failures)
    {
        string[] assemblies = Directory.GetFiles(directory, "*.dll", SearchOption.TopDirectoryOnly);
        if (assemblies.Length == 0)
        {
            failures.Add(new PluginLoadFailure(directory, $"Plugin directory '{directory}' contains no .dll files.", null));
            return;
        }

        // One load context per plugin directory (D4 isolation), rooted at the
        // first assembly found so dependency resolution has a .deps.json to
        // start from if the plugin published one.
        PluginAssemblyLoadContext context;
        try
        {
            context = new PluginAssemblyLoadContext(Path.GetFileName(directory), assemblies[0]);
        }
        catch (Exception ex)
        {
            failures.Add(new PluginLoadFailure(directory, $"Could not create a load context for plugin directory '{directory}': {ex.Message}", ex));
            return;
        }

        foreach (string assemblyPath in assemblies.OrderBy(a => a, StringComparer.Ordinal))
        {
            LoadAssemblyInto(context, assemblyPath, providers, failures);
        }
    }

    private static void LoadPluginAssembly(string assemblyPath, List<IDeviceProvider> providers, List<PluginLoadFailure> failures)
    {
        PluginAssemblyLoadContext context;
        try
        {
            context = new PluginAssemblyLoadContext(Path.GetFileNameWithoutExtension(assemblyPath), assemblyPath);
        }
        catch (Exception ex)
        {
            failures.Add(new PluginLoadFailure(assemblyPath, $"Could not create a load context for '{assemblyPath}': {ex.Message}", ex));
            return;
        }

        LoadAssemblyInto(context, assemblyPath, providers, failures);
    }

    private static void LoadAssemblyInto(PluginAssemblyLoadContext context, string assemblyPath, List<IDeviceProvider> providers, List<PluginLoadFailure> failures)
    {
        Assembly assembly;
        try
        {
            assembly = context.LoadFromAssemblyPath(assemblyPath);
        }
        catch (Exception ex) when (ex is BadImageFormatException or FileNotFoundException or FileLoadException or IOException)
        {
            // Covers "not a .NET assembly at all" (e.g. a stray text file
            // renamed .dll, or a native DLL) as well as an assembly that
            // references a runtime/platform component this process does not
            // have (the "missing ASCOM Platform" case D4 calls out by name).
            failures.Add(new PluginLoadFailure(assemblyPath, $"'{assemblyPath}' could not be loaded as a .NET assembly: {ex.Message}", ex));
            return;
        }

        Type[] types;
        try
        {
            // GetTypes() (not GetExportedTypes()) so a ReflectionTypeLoadException
            // -- the documented failure mode when a dependency such as the ASCOM
            // Platform's COM registration is missing -- can be caught and its
            // partially-loaded Types array salvaged below.
            types = assembly.GetTypes();
        }
        catch (ReflectionTypeLoadException ex)
        {
            // Some types in the assembly failed to load (typically a missing
            // dependency such as the ASCOM Platform's COM registration).
            // Salvage whatever types DID load rather than discarding the
            // whole assembly -- consistent with never returning an empty
            // result silently.
            types = ex.Types.Where(t => t != null).Cast<Type>().ToArray();
            string detail = string.Join("; ", ex.LoaderExceptions.Where(e => e != null).Select(e => e!.Message).Distinct());
            failures.Add(new PluginLoadFailure(assemblyPath, $"Some types in '{assembly.GetName().Name}' failed to load: {detail}", ex));
        }

        bool foundAnyProvider = false;
        foreach (Type type in types)
        {
            if (!IsInstantiableProvider(type))
            {
                continue;
            }

            foundAnyProvider = true;
            try
            {
                if (Activator.CreateInstance(type) is IDeviceProvider provider)
                {
                    providers.Add(provider);
                }
                else
                {
                    failures.Add(new PluginLoadFailure(assemblyPath, $"'{type.FullName}' implements {nameof(IDeviceProvider)} but could not be constructed as one.", null));
                }
            }
            catch (Exception ex)
            {
                Exception effective = ex is TargetInvocationException { InnerException: not null } tie ? tie.InnerException! : ex;
                failures.Add(new PluginLoadFailure(assemblyPath, $"'{type.FullName}' threw from its constructor: {effective.Message}", effective));
            }
        }

        if (!foundAnyProvider)
        {
            failures.Add(new PluginLoadFailure(assemblyPath, $"'{assembly.GetName().Name}' does not contain any usable {nameof(IDeviceProvider)} implementation.", null));
        }
    }

    private static bool IsInstantiableProvider(Type type)
    {
        if (!typeof(IDeviceProvider).IsAssignableFrom(type))
        {
            return false;
        }

        if (!type.IsClass || type.IsAbstract || type.IsGenericTypeDefinition)
        {
            return false;
        }

        if (!type.IsPublic && !type.IsNestedPublic)
        {
            return false;
        }

        return type.GetConstructor(Type.EmptyTypes) != null;
    }
}
