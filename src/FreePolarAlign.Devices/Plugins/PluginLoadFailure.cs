namespace FreePolarAlign.Devices.Plugins;

/// <summary>
/// One plugin that failed to load or instantiate. D4 is explicit that load
/// failures must be surfaced clearly rather than swallowed -- a missing ASCOM
/// Platform assembly, a plugin built against a stale contract version, or a
/// provider that throws from its constructor must all produce a readable
/// message here, never an empty <see cref="PluginLoadResult.Providers"/> list
/// with no explanation.
/// </summary>
/// <param name="Source">
/// The file or directory the failure came from, for diagnostics (e.g. the
/// assembly path, or the plugin subdirectory).
/// </param>
/// <param name="Message">A human-readable explanation, safe to show directly to a user.</param>
/// <param name="Exception">The underlying exception, if any, for logs.</param>
public sealed record PluginLoadFailure(string Source, string Message, Exception? Exception);
