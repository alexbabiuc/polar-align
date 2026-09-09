using FreePolarAlign.Imaging.Fits;
using WatneyAstrometry.Core;
using WatneyAstrometry.Core.Exceptions;
using WatneyAstrometry.Core.QuadDb;
using WatneyAstrometry.Core.Types;

namespace FreePolarAlign.Solving;

/// <summary>
/// <see cref="ISolver"/> adapter over Watney (D3), the embedded default solver.
/// Watney is pure managed IL with no P/Invoke and no native dependency, so it
/// is used directly as a library rather than a subprocess (D3's "Verified
/// 2026-09-08" note).
/// </summary>
/// <remarks>
/// Two contract mismatches are bridged entirely inside this adapter, exactly
/// as D3 records them: Watney expresses a scale hint as a field-radius range
/// (half the frame diagonal, degrees) rather than arcsec/pixel, and it has no
/// timeout of its own, only a <see cref="CancellationToken"/> -- so
/// <see cref="PlateSolveRequest.Timeout"/> becomes a linked
/// <see cref="CancellationTokenSource"/> here.
/// </remarks>
public sealed class WatneyPlateSolver : ISolver, IDisposable
{
    private readonly Lazy<IQuadDatabase> _quadDatabase;

    /// <param name="quadDatabaseDirectory">
    /// Directory holding one or more extracted Watney quad-database packs
    /// (D13). Opened lazily on first use and reused across solves: re-opening
    /// it is the expensive part of a solve, per Watney's own
    /// <c>Solver.SolveFieldAsync</c> ("this can take time if the factory
    /// method is actually reading the database files at this moment").
    /// </param>
    public WatneyPlateSolver(string quadDatabaseDirectory)
    {
        if (string.IsNullOrWhiteSpace(quadDatabaseDirectory))
        {
            throw new ArgumentException("Quad database directory must be provided.", nameof(quadDatabaseDirectory));
        }

        _quadDatabase = new Lazy<IQuadDatabase>(() => new CompactQuadDatabase().UseDataSource(quadDatabaseDirectory));
    }

    public string Name => "Watney";

    public async Task<PlateSolveResult> SolveAsync(PlateSolveRequest request, CancellationToken cancellationToken = default)
    {
        FitsImage image;
        try
        {
            image = FitsFile.Read(request.ImagePath);
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or KeyNotFoundException or FormatException)
        {
            return PlateSolveResult.Failed(PlateSolveFailureReason.InvalidImage, $"Could not read '{request.ImagePath}' as FITS: {ex.Message}");
        }

        ISearchStrategy strategy = BuildStrategy(request, image.Width, image.Height);

        var solver = new Solver();
        solver.UseQuadDatabase(() => _quadDatabase.Value);

        using var timeoutCts = new CancellationTokenSource();
        if (request.Timeout is { } timeout)
        {
            timeoutCts.CancelAfter(timeout);
        }

        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);

        SolveResult watneyResult;
        try
        {
            watneyResult = await solver.SolveFieldAsync(request.ImagePath, strategy, new SolverOptions(), linkedCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            PlateSolveFailureReason reason = CancellationClassification.Classify(cancellationToken, timeoutCts);
            return PlateSolveResult.Failed(reason, CancellationClassification.Message(reason, Name));
        }
        catch (SolverInputException ex)
        {
            // Watney throws this for a missing/unreadable file or an
            // extension it has no registered reader for; both are "we could
            // not treat this as a usable image", not a solver-internal fault.
            return PlateSolveResult.Failed(PlateSolveFailureReason.InvalidImage, ex.Message);
        }
        catch (QuadDatabaseException ex)
        {
            return PlateSolveResult.Failed(PlateSolveFailureReason.SolverError, $"Watney quad database error: {ex.Message}");
        }
        catch (SolverException ex)
        {
            return PlateSolveResult.Failed(PlateSolveFailureReason.SolverError, ex.Message);
        }
        catch (Exception ex)
        {
            // Not one of Watney's documented typed exceptions (D3 says it
            // reports failures as "clean typed exceptions"), but the contract
            // forbids letting anything escape SolveAsync as an exception.
            return PlateSolveResult.Failed(PlateSolveFailureReason.SolverError, $"Watney reported an unexpected error: {ex.Message}");
        }

        return MapResult(watneyResult, cancellationToken, timeoutCts);
    }

    internal static PlateSolveResult MapResult(SolveResult watneyResult, CancellationToken callerToken, CancellationTokenSource timeoutCts)
    {
        if (watneyResult.Canceled)
        {
            PlateSolveFailureReason reason = CancellationClassification.Classify(callerToken, timeoutCts);
            return PlateSolveResult.Failed(reason, CancellationClassification.Message(reason, "Watney"));
        }

        if (!watneyResult.Success)
        {
            // "No match" is an expected, common outcome (ISolver's doc comment
            // on NoMatchFound), never an exception. Zero detected stars gets
            // its own, more actionable reason.
            return watneyResult.StarsDetected == 0
                ? PlateSolveResult.Failed(PlateSolveFailureReason.NoStarsDetected, "No stars were detected in the image.")
                : PlateSolveResult.Failed(PlateSolveFailureReason.NoMatchFound,
                    $"No index match found after searching {watneyResult.AreasSearched} area(s) using {watneyResult.StarsUsedInSolve} of {watneyResult.StarsDetected} detected star(s).");
        }

        Solution solution = watneyResult.Solution!;
        Solution.FitsHeaderFields headers = solution.FitsHeaders;

        return PlateSolveResult.Succeeded(new PlateSolveSolution(
            CenterRaDegrees: solution.PlateCenter.Ra,
            CenterDecDegrees: solution.PlateCenter.Dec,
            PixelScaleArcsecPerPixel: solution.PixelScale,
            RotationDegrees: solution.Orientation,
            Cd1_1: headers.CD1_1,
            Cd1_2: headers.CD1_2,
            Cd2_1: headers.CD2_1,
            Cd2_2: headers.CD2_2,
            MatchedStarCount: watneyResult.StarsUsedInSolve,
            SolveDuration: watneyResult.TimeSpent));
    }

    /// <summary>
    /// Radii below this are not reached by Watney's own default blind ladder,
    /// which stops at 0.703 degrees, so a fully blind solve of anything smaller
    /// than about a 1.4 degree frame fails outright without lowering it.
    /// </summary>
    internal const double BlindMinimumRadiusDegrees = 0.15;

    /// <summary>
    /// How many density passes either side of the one implied by the frame to
    /// try. Watney defaults this to zero -- a single pass -- and that turns out
    /// to be the single largest cause of otherwise inexplicable solve failures:
    /// the pack's passes are built at fixed star densities, and a frame whose
    /// detected density falls between two of them matches neither.
    ///
    /// Measured across the bundled 00-07 pack: at zero offset, frames of 2.3 and
    /// 1.2 degrees diagonal failed to solve at all; at +/-2 every field from 7.4
    /// down to 1.2 degrees solved, in under a second. The cost is a larger
    /// search, which the timings say is worth paying.
    /// </summary>
    internal const uint DensityOffsetPasses = 2;

    /// <summary>
    /// Above this fractional scale tolerance the hint is treated as a claim
    /// rather than a measurement, and ignored in favour of a blind ladder.
    ///
    /// This is not timidity about the hint: Watney searches a *discrete*
    /// sequence of field radii, halving from a start value, so a fractional
    /// tolerance cannot be expressed as a range the way it can with other
    /// solvers. Handing it a band collapses the ladder to a single radius, which
    /// is right when the scale is known and wrong when it is merely guessed --
    /// and a user's stated focal length is routinely several percent out (see
    /// EquipmentProfile).
    /// </summary>
    internal const double TrustedScaleToleranceFraction = 0.15;

    internal static ISearchStrategy BuildStrategy(PlateSolveRequest request, int imageWidth, int imageHeight)
    {
        bool hasPositionHint = request.ApproximateRaDegrees is not null && request.ApproximateDecDegrees is not null;

        double? trustedRadius = null;
        if (request.ApproximateScaleArcsecPerPixel is { } scale
            && (request.ScaleToleranceFraction ?? 0.0) <= TrustedScaleToleranceFraction)
        {
            trustedRadius = ComputeFieldRadiusDegrees(scale, imageWidth, imageHeight);
        }

        if (hasPositionHint)
        {
            var options = new NearbySearchStrategyOptions
            {
                MaxNegativeDensityOffset = DensityOffsetPasses,
                MaxPositiveDensityOffset = DensityOffsetPasses,
            };

            if (request.SearchRadiusDegrees is { } searchRadius)
            {
                options.SearchAreaRadiusDegrees = searchRadius;
            }

            if (trustedRadius is { } radius)
            {
                // Pinned rather than bracketed: Watney tries the endpoints of
                // this range, so a band around the true radius can miss it
                // entirely while the exact value matches immediately.
                options.MinFieldRadiusDegrees = radius;
                options.MaxFieldRadiusDegrees = radius;
            }

            var center = new EquatorialCoords(request.ApproximateRaDegrees!.Value, request.ApproximateDecDegrees!.Value);
            return new NearbySearchStrategy(center, options);
        }

        var blindOptions = new BlindSearchStrategyOptions
        {
            MaxNegativeDensityOffset = DensityOffsetPasses,
            MaxPositiveDensityOffset = DensityOffsetPasses,
        };

        if (trustedRadius is { } blindRadius)
        {
            blindOptions.StartRadiusDegrees = blindRadius;

            // Just under the start value, so the ladder's single step is the
            // radius we actually want and it does not halve past it.
            blindOptions.MinRadiusDegrees = blindRadius * 0.999;
        }
        else
        {
            blindOptions.MinRadiusDegrees = BlindMinimumRadiusDegrees;
        }

        return new BlindSearchStrategy(blindOptions);
    }

    /// <summary>
    /// Watney searches by field radius -- half the frame diagonal, in degrees --
    /// not by pixel scale (D3). This is the one place that conversion happens.
    /// </summary>
    internal static double ComputeFieldRadiusDegrees(double scaleArcsecPerPixel, int imageWidth, int imageHeight)
    {
        double diagonalPixels = Math.Sqrt((double)imageWidth * imageWidth + (double)imageHeight * imageHeight);
        double fieldRadiusDegrees = 0.5 * diagonalPixels * scaleArcsecPerPixel / 3600.0;

        // Watney's own option setters throw outside (0, 30] degrees; clamp here
        // rather than let a pathological request throw from inside an adapter
        // that is contractually not supposed to throw.
        return Math.Clamp(fieldRadiusDegrees, 1e-3, 30.0);
    }

    public void Dispose()
    {
        if (_quadDatabase.IsValueCreated && _quadDatabase.Value is IDisposable disposable)
        {
            disposable.Dispose();
        }
    }
}
