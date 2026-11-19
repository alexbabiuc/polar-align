namespace FreePolarAlign.Solving;

/// <summary>
/// A single request to plate-solve one image. Solver-specific behaviour (index
/// selection, quad databases, subprocess flags, ...) stays entirely behind the
/// implementation; this contract only carries what any solver can act on
/// (D3: "All solver-specific behaviour stays behind the interface, including
/// scale hints, timeouts, and failure modes").
/// </summary>
/// <param name="ImagePath">Path to the FITS image to solve.</param>
/// <param name="ApproximateScaleArcsecPerPixel">
/// Best known pixel scale, if any. Narrows the search and is required by some
/// solvers for a blind solve to be tractable in bounded time.
/// </param>
/// <param name="ScaleToleranceFraction">
/// Fractional tolerance around <see cref="ApproximateScaleArcsecPerPixel"/>,
/// e.g. 0.1 for +/-10%. Ignored if no scale hint is given.
/// </param>
/// <param name="ApproximateRaDegrees">Approximate field center RA (ICRS, degrees), if known.</param>
/// <param name="ApproximateDecDegrees">Approximate field center Dec (ICRS, degrees), if known.</param>
/// <param name="SearchRadiusDegrees">
/// Radius around the approximate center to search, if a position hint is given.
/// </param>
/// <param name="Timeout">
/// Maximum time the solver may spend before giving up. A solver that cannot
/// finish in time must fail with <see cref="PlateSolveFailureReason.Timeout"/>
/// rather than block indefinitely.
/// </param>
public sealed record PlateSolveRequest(
    string ImagePath,
    double? ApproximateScaleArcsecPerPixel = null,
    double? ScaleToleranceFraction = null,
    double? ApproximateRaDegrees = null,
    double? ApproximateDecDegrees = null,
    double? SearchRadiusDegrees = null,
    TimeSpan? Timeout = null);

/// <summary>
/// Why a solve did not produce a usable result. Callers must be able to react
/// sensibly without knowing which solver ran (D3).
/// </summary>
public enum PlateSolveFailureReason
{
    /// <summary>The solver did not finish within the requested timeout.</summary>
    Timeout,

    /// <summary>Too few stars were detected in the image to attempt a match.</summary>
    NoStarsDetected,

    /// <summary>Stars were detected but no index match was found.</summary>
    NoMatchFound,

    /// <summary>The image could not be read or was not a valid FITS file.</summary>
    InvalidImage,

    /// <summary>The solver process or library reported an internal error.</summary>
    SolverError,

    /// <summary>The request was cancelled via the supplied <see cref="CancellationToken"/>.</summary>
    Cancelled
}

/// <summary>
/// A successful solve. The CD matrix is carried directly (rather than, say,
/// CDELT+CROTA2) because D12 derives correction direction and parity from its
/// determinant, and every solver-independent consumer needs the same form.
/// </summary>
public sealed record PlateSolveSolution(
    double CenterRaDegrees,
    double CenterDecDegrees,
    double PixelScaleArcsecPerPixel,
    double RotationDegrees,
    double Cd1_1,
    double Cd1_2,
    double Cd2_1,
    double Cd2_2,
    int MatchedStarCount,
    TimeSpan SolveDuration);

/// <summary>
/// The outcome of a plate solve: either a <see cref="PlateSolveSolution"/> or a
/// <see cref="PlateSolveFailureReason"/> with a human-readable message. Modeled
/// as a closed result type rather than throwing, since "no match" is an
/// expected, common outcome and not exceptional control flow.
///
/// A failure says what happened twice over (D46): <see cref="Message"/> is one
/// short sentence of this application's, fit for a panel the user glances at,
/// and <see cref="Detail"/> is whatever the solver itself said, verbatim, for
/// the log. ASTAP's console output for one failed frame ran to 123 lines; it
/// is what diagnoses the failure and what nobody should have to read on screen.
/// </summary>
public sealed record PlateSolveResult
{
    private PlateSolveResult(bool success, PlateSolveSolution? solution, PlateSolveFailureReason? failureReason, string? message)
    {
        Success = success;
        Solution = solution;
        FailureReason = failureReason;
        Message = message;
    }

    public bool Success { get; }

    public PlateSolveSolution? Solution { get; }

    public PlateSolveFailureReason? FailureReason { get; }

    /// <summary>
    /// One sentence, ours: what failed and, where there is one, what to do.
    /// A solver's own one-line reason may be quoted in it; its full output never is.
    /// </summary>
    public string? Message { get; }

    /// <summary>
    /// The solver's own output, verbatim, or null when it said nothing beyond
    /// <see cref="Message"/>. For the log only.
    /// </summary>
    public string? Detail { get; private init; }

    /// <summary>
    /// Which solver produced this, when something routing between solvers has
    /// said so; null otherwise, and then the solver called is the one that
    /// answered. Carried so the session log can say, frame by frame, which
    /// solver a reading came from.
    /// </summary>
    public string? SolverName { get; private init; }

    public static PlateSolveResult Succeeded(PlateSolveSolution solution) =>
        new(true, solution, null, null);

    /// <param name="detail">The solver's own output, for the log; blank is taken as none.</param>
    public static PlateSolveResult Failed(PlateSolveFailureReason reason, string message, string? detail = null) =>
        new(false, null, reason, message) { Detail = string.IsNullOrWhiteSpace(detail) ? null : detail.Trim() };

    /// <summary>This result, attributed to <paramref name="solverName"/>.</summary>
    public PlateSolveResult From(string solverName) =>
        new(Success, Solution, FailureReason, Message) { SolverName = solverName, Detail = Detail };

    /// <summary>
    /// <paramref name="text"/> when it is short enough to quote in a
    /// <see cref="Message"/> -- one line of at most <paramref name="maximumLength"/>
    /// characters -- and null otherwise. A solver's one-line reason ("Not
    /// enough stars.") is worth having on screen; a console transcript is not.
    /// </summary>
    public static string? QuotableLine(string? text, int maximumLength = 120)
    {
        string? trimmed = text?.Trim();
        return string.IsNullOrEmpty(trimmed) || trimmed.Length > maximumLength || trimmed.Contains('\n')
            ? null
            : trimmed;
    }
}

/// <summary>
/// Abstracts a plate-solving backend (D3). The engine must never assume which
/// implementation is in use; every solver-specific concern (index databases,
/// subprocess invocation, library API) lives behind this interface.
/// </summary>
public interface ISolver
{
    /// <summary>Human-readable solver name, for logs and diagnostics only.</summary>
    string Name { get; }

    Task<PlateSolveResult> SolveAsync(PlateSolveRequest request, CancellationToken cancellationToken = default);
}
