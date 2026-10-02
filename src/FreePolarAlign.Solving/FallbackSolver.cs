namespace FreePolarAlign.Solving;

/// <summary>
/// Tries a chosen solver first and hands the frame to another when it fails.
///
/// The second solver gets every failure except a cancellation, including a
/// timeout and an internal error. The two are different programs with different
/// catalogues and different ways of failing -- an external solver can be
/// missing, misconfigured, or short of a star database for this field size --
/// and none of that says anything about whether the other one can solve the
/// frame. The cost is time: a frame both refuse can take two timeouts to
/// report.
///
/// Does not own either solver: the fallback is typically shared between
/// several of these, and disposing it here would close it under the others.
/// </summary>
public sealed class FallbackSolver : ISolver
{
    private readonly ISolver _primary;
    private readonly ISolver _fallback;
    private readonly Action<string>? _log;

    /// <param name="log">
    /// Called when the fallback is used and with what it produced, so the
    /// session log says which solver actually answered.
    /// </param>
    public FallbackSolver(ISolver primary, ISolver fallback, Action<string>? log = null)
    {
        _primary = primary ?? throw new ArgumentNullException(nameof(primary));
        _fallback = fallback ?? throw new ArgumentNullException(nameof(fallback));
        _log = log;
    }

    public string Name => _primary.Name;

    public async Task<PlateSolveResult> SolveAsync(PlateSolveRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        PlateSolveResult first = await _primary.SolveAsync(request, cancellationToken).ConfigureAwait(false);
        if (first.Success || first.FailureReason == PlateSolveFailureReason.Cancelled)
        {
            return first;
        }

        cancellationToken.ThrowIfCancellationRequested();

        _log?.Invoke($"{_primary.Name} did not solve the frame ({first.FailureReason}): {first.Message} Trying {_fallback.Name}.");

        PlateSolveResult second = await _fallback.SolveAsync(request, cancellationToken).ConfigureAwait(false);
        if (second.Success)
        {
            _log?.Invoke($"{_fallback.Name} solved the frame {_primary.Name} could not.");
            return second;
        }

        if (second.FailureReason == PlateSolveFailureReason.Cancelled)
        {
            return second;
        }

        // The fallback's reason is the one reported, since it is the last word
        // on the frame, but both messages are kept: "ASTAP is not installed"
        // is the actionable half more often than the star pattern is.
        return PlateSolveResult.Failed(
            second.FailureReason!.Value,
            $"{_primary.Name}: {first.Message} {_fallback.Name}: {second.Message}");
    }
}
