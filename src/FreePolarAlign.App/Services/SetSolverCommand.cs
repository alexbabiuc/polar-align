using FreePolarAlign.Session;

namespace FreePolarAlign.App.Services;

/// <summary>
/// <c>--set-solver &lt;name&gt;</c>: sets the solver for every frame (D42), saves,
/// and exits without opening a window. The installer runs it
/// when no index pack was chosen (D39).
///
/// It goes through the settings store rather than having the installer write
/// JSON, so D30's rules and the store's atomic write apply, and nothing else
/// the user has set is touched.
/// </summary>
internal static class SetSolverCommand
{
    public const string Switch = "--set-solver";

    public const int Succeeded = 0;
    public const int BadArguments = 2;
    public const int SettingsUnreadable = 3;
    public const int SaveFailed = 4;

    /// <returns>The process exit code, or null when the arguments do not ask for this command.</returns>
    public static int? TryRun(IReadOnlyList<string> args, ISettingsStore store, TextWriter error)
    {
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(error);

        if (args.Count == 0 || !string.Equals(args[0], Switch, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        if (args.Count != 2 || !TryParseSolver(args[1], out SolverKind solver))
        {
            error.WriteLine($"Usage: {Switch} <{string.Join("|", Enum.GetNames<SolverKind>())}>");
            return BadArguments;
        }

        // A file that exists and could not be read comes back as the defaults.
        // Saving over it would replace a site, optics and everything else with
        // nothing, from a file that may only be from a newer version.
        SettingsLoadResult loaded = store.Load();
        if (ReferenceEquals(loaded.Settings, AppSettings.Empty) && loaded.Warning is not null)
        {
            error.WriteLine($"{loaded.Warning} The solver was not set, and the file has been left as it is.");
            return SettingsUnreadable;
        }

        // Null is how the settings say Internal, so a choice of Internal reads
        // back exactly as a fresh install's would.
        SolverKind? stored = solver == SolverKind.Internal ? null : solver;
        string? problem = store.Save(loaded.Settings with { Solver = stored });
        if (problem is not null)
        {
            error.WriteLine(problem);
            return SaveFailed;
        }

        return Succeeded;
    }

    /// <summary>
    /// By name only, ignoring case. <see cref="Enum.TryParse{TEnum}(string, bool, out TEnum)"/>
    /// also accepts numbers, and D30 stores solvers by name so that a number can
    /// never pick one.
    /// </summary>
    private static bool TryParseSolver(string text, out SolverKind solver)
    {
        solver = default;
        string? name = Enum.GetNames<SolverKind>()
            .FirstOrDefault(candidate => string.Equals(candidate, text, StringComparison.OrdinalIgnoreCase));

        return name is not null && Enum.TryParse(name, out solver);
    }
}
