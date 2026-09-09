using FreePolarAlign.Solving;
using WatneyAstrometry.Core;
using Xunit;

namespace FreePolarAlign.Tests.Solving;

/// <summary>
/// D3: "use BlindSearchStrategy when no position hint is given,
/// NearbySearchStrategy when Approximate*/SearchRadiusDegrees are supplied."
/// These pin that selection down without needing a solve.
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
    public void PositionHint_SelectsNearbySearch()
    {
        var request = NoHints with { ApproximateRaDegrees = 83.8, ApproximateDecDegrees = -5.4 };

        ISearchStrategy strategy = WatneyPlateSolver.BuildStrategy(request, imageWidth: 1000, imageHeight: 1000);

        var nearby = Assert.IsType<NearbySearchStrategy>(strategy);
        Assert.Equal(83.8, nearby.SearchCenter.Ra, precision: 9);
        Assert.Equal(-5.4, nearby.SearchCenter.Dec, precision: 9);
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
        var request = NoHints with { ApproximateRaDegrees = 10.0, ApproximateDecDegrees = 20.0, SearchRadiusDegrees = 4.5 };

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
            NoHints with { ApproximateRaDegrees = 10.0, ApproximateDecDegrees = 20.0 },
            imageWidth: 1000, imageHeight: 1000));
        Assert.Equal(WatneyPlateSolver.DensityOffsetPasses, nearby.Options.MaxNegativeDensityOffset);
        Assert.Equal(WatneyPlateSolver.DensityOffsetPasses, nearby.Options.MaxPositiveDensityOffset);

        Assert.True(WatneyPlateSolver.DensityOffsetPasses > 0);
    }
}
