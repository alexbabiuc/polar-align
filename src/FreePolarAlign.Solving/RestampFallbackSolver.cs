using FreePolarAlign.Imaging.Detection;
using FreePolarAlign.Imaging.Fits;

namespace FreePolarAlign.Solving;

/// <summary>
/// Wraps another solver and, when it cannot match a frame, gives it a second
/// chance at a redrawn one.
///
/// A frame that a solver rejects is not necessarily a frame without stars in
/// it. Measured on real frames from a 105 mm lens and an ASI290MM: of four
/// exposures of the same sky, two solved immediately and two failed with
/// NoMatchFound while detecting 64 and 95 stars -- and one of those two solves
/// to within 0.4 arcminutes of nova.astrometry.net's answer once the same stars
/// are handed over as a clean picture. The sky was never the problem, nor the
/// focus, nor the index: a synthetic field built from catalogue stars at the
/// same position solved in half a second.
///
/// So the fallback redraws rather than re-tries. The frame's stars are found
/// with this project's own detector, which measures each source by flooding
/// outward from its peak and therefore ignores single hot pixels by
/// construction, and a new frame is drawn containing nothing but those stars:
/// same positions, same brightness order, a clean background, no noise, no
/// gradient, no amp glow, no satellite trail. Everything the detector was sure
/// about is kept and everything else is gone.
///
/// It is a fallback and not the default, deliberately. The redraw can only lose
/// information -- a star the detector misses is a star the solver never sees --
/// and on frames that solve directly it sometimes solves and sometimes does
/// not. Run first, it would turn working solves into a coin toss; run second,
/// it costs a few hundred milliseconds on frames that have already failed.
/// </summary>
public sealed class RestampFallbackSolver : ISolver, IDisposable
{
    /// <summary>
    /// Detection threshold for the redraw, in noise sigmas above the local
    /// background.
    ///
    /// Lower than it might be, and deliberately: this runs only on frames the
    /// solver has already refused, where the useful thing is more stars rather
    /// than more certain ones. A spurious source costs a wrong quad among many;
    /// a missing one costs a quad that cannot be formed at all.
    /// </summary>
    private const double DetectionThresholdSigma = 4.0;

    /// <summary>
    /// Smallest source the redraw will keep, in connected pixels above the
    /// threshold. Two rather than the detector's usual three, because at 5.7
    /// arcsec/pixel a star is small, and one rather than two would let single
    /// hot pixels through -- which is the whole reason a redraw can help.
    /// </summary>
    private const int MinimumStarPixels = 2;

    /// <summary>
    /// Everything the detector measured, in flux order. Measured across the
    /// frames this was built for: a redraw of all detected stars solved where
    /// redraws of the brightest 75%, 50% and 25% did not, and the quarter-set
    /// never solved once in sixteen attempts. Fewer stars is the wrong
    /// direction -- the matcher needs quads, and quads need company.
    /// </summary>
    private const double StarFraction = 1.0;

    private readonly ISolver _inner;
    private readonly string _workingDirectory;
    private readonly Action<string>? _log;

    /// <param name="inner">The solver to try first, and to try again on the redrawn frame.</param>
    /// <param name="workingDirectory">Where the redrawn frame is written. Created on demand.</param>
    /// <param name="log">
    /// Called once when the redraw is attempted and once with its outcome, so a
    /// session log records both actions rather than only the final verdict. A
    /// user reading "could not solve" deserves to know that a second attempt was
    /// made on their behalf and what it did.
    /// </param>
    public RestampFallbackSolver(ISolver inner, string workingDirectory, Action<string>? log = null)
    {
        ArgumentNullException.ThrowIfNull(inner);
        if (string.IsNullOrWhiteSpace(workingDirectory))
        {
            throw new ArgumentException("A working directory must be provided.", nameof(workingDirectory));
        }

        _inner = inner;
        _workingDirectory = workingDirectory;
        _log = log;
    }

    public string Name => _inner.Name;

    public async Task<PlateSolveResult> SolveAsync(PlateSolveRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        PlateSolveResult direct = await _inner.SolveAsync(request, cancellationToken).ConfigureAwait(false);
        if (direct.Success || !IsWorthRedrawing(direct.FailureReason))
        {
            return direct;
        }

        cancellationToken.ThrowIfCancellationRequested();

        _log?.Invoke(
            $"Solve failed ({direct.FailureReason}): {direct.Message} " +
            "Retrying on a frame redrawn from its own detected stars.");

        string? redrawnPath = null;
        try
        {
            (redrawnPath, int starCount) = Redraw(request.ImagePath);
            if (redrawnPath is null)
            {
                _log?.Invoke("Redraw found too few stars to be worth solving; reporting the original failure.");
                return direct;
            }

            PlateSolveResult retry = await _inner
                .SolveAsync(request with { ImagePath = redrawnPath }, cancellationToken)
                .ConfigureAwait(false);

            if (retry.Success)
            {
                PlateSolveSolution solution = retry.Solution!;
                _log?.Invoke(FormattableString.Invariant($"Redrawn frame solved using {starCount} detected star(s): RA {solution.CenterRaDegrees:F4}°, Dec {solution.CenterDecDegrees:F4}°, {solution.PixelScaleArcsecPerPixel:F3} arcsec/pixel."));
                return retry;
            }

            _log?.Invoke($"Redrawn frame did not solve either ({retry.FailureReason}): {retry.Message}");

            // The original failure is the one to report. The redraw is an
            // attempt to rescue a frame, not a second opinion about it, and its
            // failure message describes a picture the user never took.
            return direct;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException
                                       or FormatException or KeyNotFoundException or NotSupportedException)
        {
            // A fallback that throws would turn a solve failure into a session
            // failure, which is a worse outcome than the one it was trying to fix.
            _log?.Invoke($"Could not redraw the frame: {ex.Message}. Reporting the original failure.");
            return direct;
        }
        finally
        {
            TryDelete(redrawnPath);
        }
    }

    /// <summary>
    /// Only the failures a different picture of the same sky could plausibly
    /// fix. A timeout, a cancellation, an unreadable file or an internal solver
    /// error all describe something other than the star pattern, and redrawing
    /// would waste the time of a user already waiting.
    /// </summary>
    private static bool IsWorthRedrawing(PlateSolveFailureReason? reason) =>
        reason is PlateSolveFailureReason.NoMatchFound or PlateSolveFailureReason.NoStarsDetected;

    private (string? Path, int StarCount) Redraw(string sourcePath)
    {
        FitsImage image = FitsFile.Read(sourcePath);

        IReadOnlyList<DetectedStar> detected = StarDetector.Detect(
            image, DetectionThresholdSigma, MinimumStarPixels);

        int take = Math.Max(1, (int)Math.Round(detected.Count * StarFraction));
        List<DetectedStar> stars = detected.Take(take).ToList();

        // Below this there is nothing for a matcher to work with, and drawing a
        // near-empty frame only spends a solve to say so.
        if (stars.Count < 8)
        {
            return (null, stars.Count);
        }

        FitsImage redrawn = RestampedFrame.Render(image.Width, image.Height, stars);

        Directory.CreateDirectory(_workingDirectory);
        string path = Path.Combine(
            _workingDirectory,
            $"{Path.GetFileNameWithoutExtension(sourcePath)}-restamped-{Guid.NewGuid():N}.fits");
        FitsFile.Write(path, redrawn);

        return (path, stars.Count);
    }

    private static void TryDelete(string? path)
    {
        if (path is null)
        {
            return;
        }

        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A leftover temporary frame is untidy, not harmful, and this runs
            // in a finally block where throwing would replace a solve result
            // with an exception.
        }
    }

    public void Dispose()
    {
        if (_inner is IDisposable disposable)
        {
            disposable.Dispose();
        }
    }
}
