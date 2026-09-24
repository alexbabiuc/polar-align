namespace FreePolarAlign.Devices;

/// <summary>
/// Turns the model names a vendor SDK enumerates into names a user can tell
/// apart.
///
/// Two cameras of the same model enumerate under the same name, and a picker
/// showing "ZWO ASI290MM Mini" twice leaves the user guessing which is on the
/// guide scope. Numbering only the duplicates keeps the common case -- one
/// camera -- showing exactly the name printed on it.
/// </summary>
public static class DeviceNaming
{
    /// <returns>
    /// One name per input, in the same order: unchanged when unique, suffixed
    /// " #1", " #2" and so on in enumeration order when not.
    /// </returns>
    public static IReadOnlyList<string> Disambiguate(IReadOnlyList<string> names)
    {
        ArgumentNullException.ThrowIfNull(names);

        var totals = names
            .GroupBy(n => n, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal);
        var seen = new Dictionary<string, int>(StringComparer.Ordinal);
        var result = new string[names.Count];

        for (int i = 0; i < names.Count; i++)
        {
            string name = names[i];
            if (totals[name] == 1)
            {
                result[i] = name;
                continue;
            }

            int ordinal = seen.TryGetValue(name, out int before) ? before + 1 : 1;
            seen[name] = ordinal;
            result[i] = $"{name} #{ordinal}";
        }

        return result;
    }
}
