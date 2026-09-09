using FreePolarAlign.Solving;
using WatneyAstrometry.Core.Types;
using Xunit;

namespace FreePolarAlign.Tests.Solving;

/// <summary>
/// Maps Watney's <see cref="SolveResult"/> onto <see cref="PlateSolveResult"/>
/// without running any real solve: <see cref="SolveResult"/> and
/// <see cref="Solution"/> are plain, publicly constructible types, so every
/// outcome (success, no stars, no match, cancelled, timed out) can be
/// fabricated directly and checked against
/// <see cref="WatneyPlateSolver.MapResult"/>.
/// </summary>
public class WatneyResultMappingTests
{
    private static Solution MakeSolution(double cd1_1, double cd1_2, double cd2_1, double cd2_2)
    {
        return new Solution(
            inputCoordinates: new EquatorialCoords(10.0, 20.0),
            imageCenter: new EquatorialCoords(10.5, 20.5),
            imageW: 1000,
            imageH: 800,
            pixelScale: 2.3,
            fieldWidth: 0.6,
            fieldHeight: 0.5,
            radius: 0.4,
            plateConstants: new PlateConstants(),
            cdelt1: cd1_1,
            cdelt2: cd2_2,
            crota1: 12.5,
            crota2: 12.5,
            cd1_1: cd1_1,
            cd2_1: cd2_1,
            cd1_2: cd1_2,
            cd2_2: cd2_2,
            crval1: 10.5,
            crval2: 20.5,
            crpix1: 500,
            crpix2: 400,
            parity: Parity.Normal);
    }

    [Fact]
    public void Success_PassesSolutionFieldsThroughHonestly()
    {
        Solution solution = MakeSolution(cd1_1: -0.0002, cd1_2: 0.0, cd2_1: 0.0, cd2_2: 0.0002);
        var watneyResult = new SolveResult
        {
            Success = true,
            Canceled = false,
            StarsDetected = 120,
            StarsUsedInSolve = 87,
            AreasSearched = 3,
            TimeSpent = TimeSpan.FromSeconds(4.25),
            Solution = solution,
        };

        PlateSolveResult result = WatneyPlateSolver.MapResult(watneyResult, CancellationToken.None, new CancellationTokenSource());

        Assert.True(result.Success);
        PlateSolveSolution mapped = result.Solution!;
        Assert.Equal(solution.PlateCenter.Ra, mapped.CenterRaDegrees);
        Assert.Equal(solution.PlateCenter.Dec, mapped.CenterDecDegrees);
        Assert.Equal(solution.PixelScale, mapped.PixelScaleArcsecPerPixel);
        Assert.Equal(solution.Orientation, mapped.RotationDegrees);
        Assert.Equal(-0.0002, mapped.Cd1_1);
        Assert.Equal(0.0, mapped.Cd1_2);
        Assert.Equal(0.0, mapped.Cd2_1);
        Assert.Equal(0.0002, mapped.Cd2_2);
        Assert.Equal(87, mapped.MatchedStarCount);
        Assert.Equal(TimeSpan.FromSeconds(4.25), mapped.SolveDuration);
    }

    [Theory]
    [InlineData(-0.0002, 0.0, 0.0, 0.0002, -1)] // normal parity: negative determinant
    [InlineData(0.0002, 0.0, 0.0, 0.0002, 1)]   // mirrored parity: positive determinant
    public void Success_PreservesCdMatrixDeterminantSign(double cd1_1, double cd1_2, double cd2_1, double cd2_2, int expectedSign)
    {
        // D12 derives correction direction/parity from the CD determinant's
        // sign, so the adapter must pass the matrix through unchanged rather
        // than, say, silently reordering or negating a term.
        Solution solution = MakeSolution(cd1_1, cd1_2, cd2_1, cd2_2);
        var watneyResult = new SolveResult { Success = true, Solution = solution };

        PlateSolveResult result = WatneyPlateSolver.MapResult(watneyResult, CancellationToken.None, new CancellationTokenSource());

        double determinant = result.Solution!.Cd1_1 * result.Solution.Cd2_2 - result.Solution.Cd1_2 * result.Solution.Cd2_1;
        Assert.Equal(expectedSign, Math.Sign(determinant));
    }

    [Fact]
    public void NoStarsDetected_MapsToNoStarsDetectedReason()
    {
        var watneyResult = new SolveResult { Success = false, Canceled = false, StarsDetected = 0 };

        PlateSolveResult result = WatneyPlateSolver.MapResult(watneyResult, CancellationToken.None, new CancellationTokenSource());

        Assert.False(result.Success);
        Assert.Equal(PlateSolveFailureReason.NoStarsDetected, result.FailureReason);
    }

    [Fact]
    public void StarsDetectedButNoSolution_MapsToNoMatchFound()
    {
        // The doc comment on ISolver.PlateSolveFailureReason.NoMatchFound
        // calls this an expected, common outcome -- it must come back as a
        // result, never an exception.
        var watneyResult = new SolveResult { Success = false, Canceled = false, StarsDetected = 40, StarsUsedInSolve = 40, AreasSearched = 200 };

        PlateSolveResult result = WatneyPlateSolver.MapResult(watneyResult, CancellationToken.None, new CancellationTokenSource());

        Assert.False(result.Success);
        Assert.Equal(PlateSolveFailureReason.NoMatchFound, result.FailureReason);
        Assert.Contains("200", result.Message);
    }

    [Fact]
    public void Canceled_ByCaller_MapsToCancelled()
    {
        using var callerCts = new CancellationTokenSource();
        callerCts.Cancel();
        var watneyResult = new SolveResult { Success = false, Canceled = true };

        PlateSolveResult result = WatneyPlateSolver.MapResult(watneyResult, callerCts.Token, new CancellationTokenSource());

        Assert.Equal(PlateSolveFailureReason.Cancelled, result.FailureReason);
    }

    [Fact]
    public void Canceled_ByTimeout_MapsToTimeout()
    {
        using var timeoutCts = new CancellationTokenSource();
        timeoutCts.Cancel();
        var watneyResult = new SolveResult { Success = false, Canceled = true };

        PlateSolveResult result = WatneyPlateSolver.MapResult(watneyResult, CancellationToken.None, timeoutCts);

        Assert.Equal(PlateSolveFailureReason.Timeout, result.FailureReason);
    }
}
