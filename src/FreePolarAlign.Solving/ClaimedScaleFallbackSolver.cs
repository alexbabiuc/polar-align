using System.Globalization;

namespace FreePolarAlign.Solving;

/// <summary>
/// Tries a claimed scale first and no scale after it (D41). A scale worked out
/// from a typed focal length is what lets a narrow field solve at all -- ASTAP's
/// own field search stops at 0.37° -- but a focal length typed wrong, as with a
/// reducer forgotten, would otherwise stop every solve until it was corrected.
/// Without a scale the solver searches as it would knowing nothing, so the
/// frames it could always solve still solve, and the first success measures the
/// real focal length. From then on the scale is a measurement and is not
/// second-guessed.
///
/// Both tries share the request's timeout, so the job's limit (D33) still bounds
/// the whole solve.
/// </summary>
public sealed class ClaimedScaleFallbackSolver : ISolver
{
    /// <summary>
    /// The widest scale tolerance that counts as a measurement. ASTAP solved two
    /// real 0.23° frames at every field height from 0.9 to 1.1 times the true one
    /// and at neither 1.2 nor 1.5 times (D41), so a scale allowed to be further out
    /// than this may be the reason nothing matched.
    /// </summary>
    public const double MeasuredScaleToleranceFraction = 0.10;

    private readonly ISolver _inner;
    private readonly TimeProvider _time;
    private readonly Action<string>? _log;

    public ClaimedScaleFallbackSolver(ISolver inner, TimeProvider timeProvider, Action<string>? log = null)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        _time = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        _log = log;
    }

    public string Name => _inner.Name;

    public async Task<PlateSolveResult> SolveAsync(PlateSolveRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        long started = _time.GetTimestamp();
        PlateSolveResult first = await _inner.SolveAsync(request, cancellationToken).ConfigureAwait(false);
        if (!ShouldRetryWithoutScale(request, first))
        {
            return first;
        }

        TimeSpan? remaining = request.Timeout - _time.GetElapsedTime(started);
        if (remaining <= TimeSpan.Zero)
        {
            return first;
        }

        _log?.Invoke(string.Format(CultureInfo.InvariantCulture,
            "No match at {0:F3}\"/pixel, the scale from the entered focal length. Trying again without a scale.",
            request.ApproximateScaleArcsecPerPixel));

        PlateSolveResult second = await _inner.SolveAsync(
            request with { ApproximateScaleArcsecPerPixel = null, ScaleToleranceFraction = null, Timeout = remaining },
            cancellationToken).ConfigureAwait(false);

        return second.Success || second.FailureReason is not { } reason
            ? second
            : PlateSolveResult.Failed(reason,
                $"Not solved at the entered focal length's scale ({first.Message}) nor without a scale ({second.Message}).");
    }

    /// <summary>
    /// Only a claimed scale and only "no match": a frame with too few stars, or
    /// one that ran out of time, did not fail for its scale.
    /// </summary>
    internal static bool ShouldRetryWithoutScale(PlateSolveRequest request, PlateSolveResult first) =>
        first.FailureReason == PlateSolveFailureReason.NoMatchFound
        && request.ApproximateScaleArcsecPerPixel is not null
        && (request.ScaleToleranceFraction ?? 0.0) > MeasuredScaleToleranceFraction;
}
