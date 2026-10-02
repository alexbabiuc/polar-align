using FreePolarAlign.Session;
using FreePolarAlign.Solving;

namespace FreePolarAlign.App.Services;

/// <summary>
/// The solver the session sees: whichever the settings chose for the kind of
/// frame in hand, with the internal solver behind it for every frame the chosen
/// one cannot solve.
///
/// A frame is "near" when the request carries a position hint -- the session
/// adds one exactly when a mount is connected and reporting -- and "blind"
/// otherwise. That is the same line the settings window draws.
///
/// Reconfigured in place rather than rebuilt, because the session holds this
/// instance for the life of the application: a choice saved in the settings
/// window takes effect from the next frame without reconnecting anything.
/// </summary>
public sealed class SelectedSolver : ISolver, IDisposable
{
    private readonly ISolver _internal;
    private readonly Action<string>? _log;
    private readonly Func<string?, string> _resolveAstap;

    private volatile Routes _routes;
    private (SolverKind Blind, SolverKind Near, string? AstapPath)? _configuredFrom;

    /// <param name="internalSolver">
    /// The built-in solver: used alone when it is the choice, and as the
    /// fallback behind any other. Owned, and disposed with this.
    /// </param>
    /// <param name="resolveAstap">
    /// Turns the stored ASTAP path into an executable. A parameter so tests do
    /// not depend on what is installed on the machine running them.
    /// </param>
    public SelectedSolver(ISolver internalSolver, Action<string>? log = null, Func<string?, string>? resolveAstap = null)
    {
        _internal = internalSolver ?? throw new ArgumentNullException(nameof(internalSolver));
        _log = log;
        _resolveAstap = resolveAstap ?? AstapPlateSolver.ResolveExecutable;
        _routes = new Routes(_internal, _internal);
    }

    public string Name => _routes.Blind.Name;

    /// <summary>
    /// Applies the solver choices in <paramref name="settings"/>. Cheap to call
    /// on every settings change: nothing is rebuilt or logged unless a solver
    /// choice or the ASTAP path actually changed.
    /// </summary>
    public void Configure(AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        var wanted = (Blind: settings.BlindSolver ?? SolverKind.Internal, Near: settings.NearSolver ?? SolverKind.Internal, settings.AstapPath);
        if (_configuredFrom == wanted)
        {
            return;
        }

        _configuredFrom = wanted;
        ISolver blind = Build(wanted.Blind, settings, "blind");
        ISolver near = wanted.Near == wanted.Blind ? blind : Build(wanted.Near, settings, "near");

        // One reference swap, so a solve already under way finishes on the
        // route it started on and the next one takes the new pair whole.
        _routes = new Routes(blind, near);
    }

    public Task<PlateSolveResult> SolveAsync(PlateSolveRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        Routes routes = _routes;
        bool near = request.ApproximateRaDegrees is not null && request.ApproximateDecDegrees is not null;
        return (near ? routes.Near : routes.Blind).SolveAsync(request, cancellationToken);
    }

    private ISolver Build(SolverKind kind, AppSettings settings, string job)
    {
        switch (kind)
        {
            case SolverKind.Internal:
                _log?.Invoke($"The {job} solver is {_internal.Name}.");
                return _internal;

            case SolverKind.Astap:
                string executable = _resolveAstap(settings.AstapPath);
                _log?.Invoke($"The {job} solver is ASTAP at '{executable}', with {_internal.Name} for any frame it cannot solve.");
                return new FallbackSolver(new AstapPlateSolver(executable), _internal, _log);

            default:
                // Remembered so the choice survives until the adapter exists,
                // and solved meanwhile by the solver that does.
                _log?.Invoke($"The {job} solver chosen, {kind}, is not available in this version; {_internal.Name} will solve these frames.");
                return _internal;
        }
    }

    public void Dispose()
    {
        if (_internal is IDisposable disposable)
        {
            disposable.Dispose();
        }
    }

    private sealed record Routes(ISolver Blind, ISolver Near);
}
