using FreePolarAlign.Solving;
using WatneyAstrometry.Core;
using Xunit;

namespace FreePolarAlign.Tests.Solving;

/// <summary>
/// Which search strategy a request selects, pinned down without needing a solve.
///
/// The rule is not simply "position hint means nearby search". A nearby search
/// is only usable when the field radius can also be pinned, because Watney
/// defaults that range to 0-2 degrees and searches only its endpoints -- so a
/// position hint without a trustworthy scale would silently exclude any wider
/// frame. See D3's tuning section.
/// </summary>
public class SearchStrategySelectionTests
{
    private static readonly PlateSolveRequest NoHints = new("image.fits");

    [Fact]
    public void NoPositionHint_SelectsBlindSearch()
    {
        ISearchStrategy strategy = WatneyPlateSolver.BuildStrategy(NoHints, imageWidth: 1000, imageHeight: 1000);

        Assert.IsType<BlindSearchStrategy>(strategy);
    }

    [Fact]
    public void PositionAndTrustedScaleHint_SelectsNearbySearch()
    {
        var request = NoHints with
        {
            ApproximateRaDegrees = 83.8,
            ApproximateDecDegrees = -5.4,
            ApproximateScaleArcsecPerPixel = 2.0,
            ScaleToleranceFraction = 0.05,
        };

        ISearchStrategy strategy = WatneyPlateSolver.BuildStrategy(request, imageWidth: 1000, imageHeight: 1000);

        var nearby = Assert.IsType<NearbySearchStrategy>(strategy);
        Assert.Equal(83.8, nearby.SearchCenter.Ra, precision: 9);
        Assert.Equal(-5.4, nearby.SearchCenter.Dec, precision: 9);
    }

    /// <summary>
    /// A position hint with no scale is deliberately discarded in favour of the
    /// blind ladder. Watney's nearby search cannot be configured to cover an
    /// unknown field size without laddering through candidate radii, which
    /// measured out slower than simply solving the frame blind -- and its
    /// default range would have excluded the field entirely.
    /// </summary>
    [Fact]
    public void PositionHintWithoutScale_FallsBackToTheBlindLadder()
    {
        var request = NoHints with { ApproximateRaDegrees = 83.8, ApproximateDecDegrees = -5.4 };

        ISearchStrategy strategy = WatneyPlateSolver.BuildStrategy(request, imageWidth: 1000, imageHeight: 1000);

        var blind = Assert.IsType<BlindSearchStrategy>(strategy);
        Assert.Equal(WatneyPlateSolver.BlindMinimumRadiusDegrees, blind.Options.MinRadiusDegrees, precision: 9);
    }

    [Fact]
    public void PartialPositionHint_FallsBackToBlindSearch()
    {
        // RA without Dec is not a usable hint.
        var request = NoHints with { ApproximateRaDegrees = 83.8 };

        ISearchStrategy strategy = WatneyPlateSolver.BuildStrategy(request, imageWidth: 1000, imageHeight: 1000);

        Assert.IsType<BlindSearchStrategy>(strategy);
    }

    [Fact]
    public void NearbySearch_UsesRequestedSearchRadius()
    {
        var request = NoHints with
        {
            ApproximateRaDegrees = 10.0,
            ApproximateDecDegrees = 20.0,
            SearchRadiusDegrees = 4.5,
            ApproximateScaleArcsecPerPixel = 2.0,
            ScaleToleranceFraction = 0.05,
        };

        var nearby = Assert.IsType<NearbySearchStrategy>(
            WatneyPlateSolver.BuildStrategy(request, imageWidth: 1000, imageHeight: 1000));

        Assert.Equal(4.5, nearby.Options.SearchAreaRadiusDegrees, precision: 9);
    }

    /// <summary>
    /// A trusted scale hint pins the field radius to a single value rather than
    /// bracketing it. Watney tries the endpoints of the range it is given, so a
    /// band around the true radius can miss it entirely while the exact value
    /// matches at once -- measured, a 25% band turned solves that succeed into
    /// NoMatchFound.
    /// </summary>
    [Fact]
    public void NearbySearch_WithTrustedScaleHint_PinsTheFieldRadius()
    {
        var request = NoHints with
        {
            ApproximateRaDegrees = 10.0,
            ApproximateDecDegrees = 20.0,
            ApproximateScaleArcsecPerPixel = 2.5,
            ScaleToleranceFraction = 0.05,
        };

        double expected = WatneyPlateSolver.ComputeFieldRadiusDegrees(2.5, 1200, 900);

        var nearby = Assert.IsType<NearbySearchStrategy>(
            WatneyPlateSolver.BuildStrategy(request, imageWidth: 1200, imageHeight: 900));

        Assert.Equal(expected, nearby.Options.MinFieldRadiusDegrees, precision: 9);
        Assert.Equal(expected, nearby.Options.MaxFieldRadiusDegrees, precision: 9);
    }

    [Fact]
    public void BlindSearch_WithTrustedScaleHint_SearchesExactlyThatRadius()
    {
        var request = NoHints with { ApproximateScaleArcsecPerPixel = 4.0, ScaleToleranceFraction = 0.05 };

        double expected = WatneyPlateSolver.ComputeFieldRadiusDegrees(4.0, 1600, 1200);

        var blind = Assert.IsType<BlindSearchStrategy>(
            WatneyPlateSolver.BuildStrategy(request, imageWidth: 1600, imageHeight: 1200));

        Assert.Equal(expected, blind.Options.StartRadiusDegrees, precision: 9);

        // Just under the start, so the halving ladder cannot step past it.
        Assert.True(blind.Options.MinRadiusDegrees < expected);
        Assert.True(blind.Options.MinRadiusDegrees > expected * 0.99);
    }

    /// <summary>
    /// A loosely-toleranced hint is a claim, not a measurement -- a user's
    /// stated focal length is routinely several percent out -- and Watney's
    /// discrete radius ladder cannot express a range. So it is ignored in favour
    /// of the blind ladder, which does cover a spread of radii.
    /// </summary>
    [Fact]
    public void BlindSearch_WithLooselyTolerancedScaleHint_FallsBackToTheBlindLadder()
    {
        var request = NoHints with { ApproximateScaleArcsecPerPixel = 4.0, ScaleToleranceFraction = 0.25 };

        var blind = Assert.IsType<BlindSearchStrategy>(
            WatneyPlateSolver.BuildStrategy(request, imageWidth: 1600, imageHeight: 1200));

        Assert.Equal(new BlindSearchStrategyOptions().StartRadiusDegrees, blind.Options.StartRadiusDegrees, precision: 9);
        Assert.Equal(WatneyPlateSolver.BlindMinimumRadiusDegrees, blind.Options.MinRadiusDegrees, precision: 9);
    }

    /// <summary>
    /// Watney's own blind ladder stops at 0.703 degrees, so without lowering the
    /// floor a fully blind solve of anything under about a 1.4 degree frame
    /// cannot match at all -- measured, 1.2 and 2.3 degree frames both failed.
    /// </summary>
    [Fact]
    public void BlindSearch_WithoutScaleHint_LowersTheRadiusFloorBelowWatneysDefault()
    {
        var defaults = new BlindSearchStrategyOptions();

        var blind = Assert.IsType<BlindSearchStrategy>(
            WatneyPlateSolver.BuildStrategy(NoHints, imageWidth: 1000, imageHeight: 1000));

        Assert.Equal(defaults.StartRadiusDegrees, blind.Options.StartRadiusDegrees, precision: 9);
        Assert.True(blind.Options.MinRadiusDegrees < defaults.MinRadiusDegrees);
        Assert.Equal(WatneyPlateSolver.BlindMinimumRadiusDegrees, blind.Options.MinRadiusDegrees, precision: 9);
    }

    /// <summary>
    /// Density offsets must be widened on every path. Watney defaults them to
    /// zero -- one density pass -- which measured out as the single largest
    /// cause of solve failures, since a frame whose star density falls between
    /// two of the pack's passes matches neither.
    /// </summary>
    [Fact]
    public void EveryStrategy_WidensTheDensityPasses()
    {
        var blind = Assert.IsType<BlindSearchStrategy>(
            WatneyPlateSolver.BuildStrategy(NoHints, imageWidth: 1000, imageHeight: 1000));
        Assert.Equal(WatneyPlateSolver.DensityOffsetPasses, blind.Options.MaxNegativeDensityOffset);
        Assert.Equal(WatneyPlateSolver.DensityOffsetPasses, blind.Options.MaxPositiveDensityOffset);

        var nearby = Assert.IsType<NearbySearchStrategy>(WatneyPlateSolver.BuildStrategy(
            NoHints with
            {
                ApproximateRaDegrees = 10.0,
                ApproximateDecDegrees = 20.0,
                ApproximateScaleArcsecPerPixel = 2.0,
                ScaleToleranceFraction = 0.05,
            },
            imageWidth: 1000, imageHeight: 1000));
        Assert.Equal(WatneyPlateSolver.DensityOffsetPasses, nearby.Options.MaxNegativeDensityOffset);
        Assert.Equal(WatneyPlateSolver.DensityOffsetPasses, nearby.Options.MaxPositiveDensityOffset);

        Assert.True(WatneyPlateSolver.DensityOffsetPasses > 0);
    }
}
