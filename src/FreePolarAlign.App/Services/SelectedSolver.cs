using FreePolarAlign.Session;
using FreePolarAlign.Solving;

namespace FreePolarAlign.App.Services;

/// <summary>
/// The solver the session sees: whichever the settings chose for the kind of
/// frame in hand, and only that one. A frame the chosen solver cannot solve is
/// reported as not solved, not handed to another (D31).
///
/// A frame is "near" when the request carries a position hint -- the session
/// adds one exactly when a mount is connected and reporting -- and "blind"
/// otherwise. That is the same line the settings window draws.
///
/// Reconfigured in place rather than rebuilt, because the session holds this
/// instance for the life of the application: a choice saved in the settings
/// window takes effect from the next frame without reconnecting anything.
///
/// Every result is stamped with the name of the solver that produced it, so
/// the session log says which solver each sample and reading came from.
///
/// The timeout is chosen the same way, from the blind or near setting, and
/// replaces whatever the request carried: the line between the two jobs is
/// drawn here, so this is where each job's limit is applied (D33).
/// </summary>
public sealed class SelectedSolver : ISolver, IDisposable
{
    private readonly ISolver _internal;
    private readonly Action<string>? _log;
    private readonly Func<SolverKind, string?, string> _resolveExecutable;
    private readonly bool _canRunWindowsSolvers;

    private volatile Routes _routes;
    private (SolverKind Blind, SolverKind Near, string? AstapPath, string? AspsPath, string? Ps3Path, TimeSpan BlindTimeout, TimeSpan NearTimeout)? _configuredFrom;

    /// <param name="internalSolver">
    /// The built-in solver, used when it is the choice. Owned, and disposed
    /// with this.
    /// </param>
    /// <param name="resolveExecutable">
    /// Turns a solver's stored path into an executable. A parameter so tests do
    /// not depend on what is installed on the machine running them.
    /// </param>
    /// <param name="canRunWindowsSolvers">
    /// Whether ASPS and PlateSolve3, which exist only for Windows, can be run
    /// here, which decides only whether choosing one elsewhere is warned
    /// about. Defaults to whether this is Windows; a parameter so both cases
    /// can be tested on either.
    /// </param>
    public SelectedSolver(
        ISolver internalSolver,
        Action<string>? log = null,
        Func<SolverKind, string?, string>? resolveExecutable = null,
        bool? canRunWindowsSolvers = null)
    {
        _internal = internalSolver ?? throw new ArgumentNullException(nameof(internalSolver));
        _log = log;
        _resolveExecutable = resolveExecutable ?? ResolveInstalled;
        _canRunWindowsSolvers = canRunWindowsSolvers ?? OperatingSystem.IsWindows();
        _routes = new Routes(_internal, _internal, AppSettings.Empty.BlindSolveTimeout, AppSettings.Empty.NearSolveTimeout);
    }

    public string Name => _routes.Blind.Name;

    /// <summary>
    /// Applies the solver choices in <paramref name="settings"/>. Cheap to call
    /// on every settings change: nothing is rebuilt or logged unless a solver
    /// choice or a solver path actually changed.
    /// </summary>
    public void Configure(AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        var wanted = (
            Blind: settings.BlindSolver ?? SolverKind.Internal,
            Near: settings.NearSolver ?? SolverKind.Internal,
            settings.AstapPath,
            settings.AspsPath,
            settings.Ps3Path,
            BlindTimeout: settings.BlindSolveTimeout,
            NearTimeout: settings.NearSolveTimeout);
        if (_configuredFrom == wanted)
        {
            return;
        }

        _configuredFrom = wanted;
        ISolver blind = Build(wanted.Blind, settings, "blind");
        ISolver near = wanted.Near == wanted.Blind ? blind : Build(wanted.Near, settings, "near");

        _log?.Invoke(FormattableString.Invariant(
            $"Solve timeouts: {wanted.BlindTimeout.TotalSeconds:G4} s blind, {wanted.NearTimeout.TotalSeconds:G4} s near."));

        // One reference swap, so a solve already under way finishes on the
        // route it started on and the next one takes the new pair whole.
        _routes = new Routes(blind, near, wanted.BlindTimeout, wanted.NearTimeout);
    }

    public async Task<PlateSolveResult> SolveAsync(PlateSolveRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        Routes routes = _routes;
        bool near = request.ApproximateRaDegrees is not null && request.ApproximateDecDegrees is not null;
        ISolver solver = near ? routes.Near : routes.Blind;
        TimeSpan timeout = near ? routes.NearTimeout : routes.BlindTimeout;

        PlateSolveResult result = await solver.SolveAsync(request with { Timeout = timeout }, cancellationToken).ConfigureAwait(false);
        return result.From(solver.Name);
    }

    private ISolver Build(SolverKind kind, AppSettings settings, string job)
    {
        if (kind == SolverKind.Internal || !Enum.IsDefined(kind))
        {
            _log?.Invoke($"The {job} solver is {_internal.Name}.");
            return _internal;
        }

        string executable = _resolveExecutable(kind, PathFor(kind, settings));
        ExternalProcessSolver external = kind switch
        {
            SolverKind.Astap => new AstapPlateSolver(executable),
            SolverKind.Asps => new AspsPlateSolver(executable),
            _ => new PlateSolve3Solver(executable),
        };

        // Said once here, where it can be acted on, rather than left to a
        // "could not start" on every frame that follows.
        if (kind is SolverKind.Asps or SolverKind.Ps3 && !_canRunWindowsSolvers)
        {
            _log?.Invoke($"The {job} solver chosen, {external.Name}, runs only on Windows. These frames will not solve " +
                         "until another solver is chosen in Settings.");
            return external;
        }

        _log?.Invoke($"The {job} solver is {external.Name} at '{executable}'.");
        return external;
    }

    private static string? PathFor(SolverKind kind, AppSettings settings) => kind switch
    {
        SolverKind.Astap => settings.AstapPath,
        SolverKind.Asps => settings.AspsPath,
        _ => settings.Ps3Path,
    };

    private static string ResolveInstalled(SolverKind kind, string? configured) => kind switch
    {
        SolverKind.Astap => AstapPlateSolver.ResolveExecutable(configured),
        SolverKind.Asps => AspsPlateSolver.ResolveExecutable(configured),
        _ => PlateSolve3Solver.ResolveExecutable(configured),
    };

    public void Dispose()
    {
        if (_internal is IDisposable disposable)
        {
            disposable.Dispose();
        }
    }

    private sealed record Routes(ISolver Blind, ISolver Near, TimeSpan BlindTimeout, TimeSpan NearTimeout);
}
