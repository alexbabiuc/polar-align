using System.Reflection;
using System.Runtime.InteropServices;

namespace FreePolarAlign.Devices.Plugins;

/// <summary>
/// Finds and loads a vendor's native library for a plugin that calls it
/// through P/Invoke, and routes that plugin's imports to it.
///
/// A vendor library is the one thing a plugin legitimately ships beside itself
/// (D24 forbids only copies of the host's own assemblies), but it cannot always
/// ship it: redistribution terms are the vendor's, and a user may already have
/// the library from the vendor's installer. So it is looked for in three places,
/// in order -- the plugin's own directory, the plugin's
/// <c>runtimes/&lt;rid&gt;/native</c> directory, then the operating system's
/// normal search -- and the first one that loads wins.
///
/// The point of doing this by hand rather than leaving it to the runtime is the
/// distinction between absent and broken. Absent -- no file anywhere -- is the
/// normal state for anyone without that brand of camera, and is reported as
/// <see cref="ProviderUnavailableException"/>, which goes to the log and not the
/// warning banner. Present but unloadable -- a 32-bit DLL beside a 64-bit
/// process, a library missing one of its own dependencies -- is a fault, and
/// its real exception is let through so it is shown. The runtime's own probing
/// reports both as the same <see cref="DllNotFoundException"/>.
/// </summary>
public sealed class NativeLibraryLocator
{
    private readonly Assembly _owner;
    private readonly string _importName;
    private readonly string _vendor;
    private readonly IReadOnlyList<string> _fileNames;
    private readonly object _gate = new();
    private IntPtr _handle;
    private bool _resolverRegistered;

    /// <param name="owner">The plugin assembly whose <c>DllImport</c>s name <paramref name="importName"/>.</param>
    /// <param name="importName">The library name used in those <c>DllImport</c> attributes.</param>
    /// <param name="vendor">Who to name in messages, e.g. "ZWO".</param>
    /// <param name="windowsFile">The file name on Windows, e.g. <c>ASICamera2.dll</c>.</param>
    /// <param name="macFile">The file name on macOS.</param>
    /// <param name="linuxFile">The file name on Linux.</param>
    public NativeLibraryLocator(
        Assembly owner, string importName, string vendor, string windowsFile, string macFile, string linuxFile)
    {
        ArgumentNullException.ThrowIfNull(owner);
        _owner = owner;
        _importName = importName;
        _vendor = vendor;
        _fileNames = OperatingSystem.IsWindows() ? new[] { windowsFile }
            : OperatingSystem.IsMacOS() ? new[] { macFile }
            : new[] { linuxFile };
    }

    /// <summary>The file this platform needs, for messages and documentation.</summary>
    public string FileName => _fileNames[0];

    /// <summary>
    /// Loads the library if it is not loaded yet, and makes the owner's imports
    /// resolve to it. Cheap after the first successful call.
    /// </summary>
    /// <exception cref="ProviderUnavailableException">The library is not present anywhere this looks.</exception>
    public void EnsureLoaded()
    {
        lock (_gate)
        {
            if (_handle == IntPtr.Zero)
            {
                _handle = Load();
            }

            if (!_resolverRegistered)
            {
                // Registered once per assembly; the runtime refuses a second
                // registration, and a second is never needed.
                NativeLibrary.SetDllImportResolver(_owner, Resolve);
                _resolverRegistered = true;
            }
        }
    }

    private IntPtr Resolve(string libraryName, Assembly assembly, DllImportSearchPath? searchPath) =>
        string.Equals(libraryName, _importName, StringComparison.Ordinal) ? _handle : IntPtr.Zero;

    private IntPtr Load()
    {
        var searched = new List<string>();

        foreach (string candidate in PrivateCandidates())
        {
            searched.Add(candidate);
            if (!File.Exists(candidate))
            {
                continue;
            }

            // Present: any failure now is a fault, not an absence, so the real
            // exception -- wrong architecture, a missing dependency -- is let
            // through rather than turned into "not found".
            return NativeLibrary.Load(candidate);
        }

        foreach (string fileName in _fileNames)
        {
            searched.Add($"{fileName} (system search path)");
            if (NativeLibrary.TryLoad(fileName, out IntPtr handle))
            {
                return handle;
            }
        }

        throw new ProviderUnavailableException(
            $"the {_vendor} library '{FileName}' was not found. To use {_vendor} cameras directly, place it in " +
            $"'{PluginDirectory()}'. Looked in: {string.Join("; ", searched)}.");
    }

    private IEnumerable<string> PrivateCandidates()
    {
        string directory = PluginDirectory();

        // Two spellings of "this platform", because they need not agree:
        // RuntimeIdentifier is whatever the runtime was built or published as,
        // and can be more specific than the folder a plugin ships under. The
        // portable form -- win-x64, win-x86, osx-arm64 -- is built from the
        // running process's own architecture, which is what decides whether a
        // library can load at all.
        string[] identifiers = new[] { PortableRuntimeIdentifier(), RuntimeInformation.RuntimeIdentifier }
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        foreach (string fileName in _fileNames)
        {
            yield return Path.Combine(directory, fileName);

            foreach (string identifier in identifiers)
            {
                yield return Path.Combine(directory, "runtimes", identifier, "native", fileName);
            }
        }
    }

    /// <summary>The portable runtime identifier for the running process, e.g. <c>win-x64</c>.</summary>
    public static string PortableRuntimeIdentifier()
    {
        string os = OperatingSystem.IsWindows() ? "win" : OperatingSystem.IsMacOS() ? "osx" : "linux";
        string architecture = RuntimeInformation.ProcessArchitecture switch
        {
            Architecture.X64 => "x64",
            Architecture.X86 => "x86",
            Architecture.Arm64 => "arm64",
            Architecture.Arm => "arm",
            Architecture other => other.ToString().ToLowerInvariant(),
        };

        return $"{os}-{architecture}";
    }

    private string PluginDirectory() =>
        Path.GetDirectoryName(_owner.Location) is { Length: > 0 } directory ? directory : AppContext.BaseDirectory;
}
