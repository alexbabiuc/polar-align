namespace FreePolarAlign.Devices.Plugins;

/// <summary>
/// The outcome of a plugin scan (D4). Both lists are always populated as
/// completely as possible: one broken plugin must not prevent the others from
/// loading, and a scan that finds nothing must say why rather than returning
/// an empty <see cref="Providers"/> list with no explanation.
/// </summary>
public sealed record PluginLoadResult(IReadOnlyList<IDeviceProvider> Providers, IReadOnlyList<PluginLoadFailure> Failures);
