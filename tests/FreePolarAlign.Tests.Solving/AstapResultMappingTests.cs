using FreePolarAlign.Imaging.Fits;
using FreePolarAlign.Imaging.Wcs;
using FreePolarAlign.Solving;
using Xunit;

namespace FreePolarAlign.Tests.Solving;

/// <summary>
/// ASTAP's exit code and stdout/stderr text are the only signal this adapter
/// gets about what happened, so the mapping from that text (and from its
/// written-back WCS) to <see cref="PlateSolveResult"/> is tested directly,
/// without starting any process.
/// </summary>
public class AstapResultMappingTests
{
    [Theory]
    [InlineData("Solving...\nNo solution found.\n", PlateSolveFailureReason.NoMatchFound)]
    [InlineData("SOLUTION NOT FOUND\n", PlateSolveFailureReason.NoMatchFound)]
    [InlineData("Too few stars detected in image.\n", PlateSolveFailureReason.NoStarsDetected)]
    [InlineData("Star detection failed: image too noisy\n", PlateSolveFailureReason.NoStarsDetected)]
    [InlineData("Segmentation fault (core dumped)\n", PlateSolveFailureReason.SolverError)]
    [InlineData("", PlateSolveFailureReason.SolverError)]
    public void ClassifyFailureText_MapsKnownPatterns(string output, PlateSolveFailureReason expected)
    {
        PlateSolveResult result = AstapPlateSolver.ClassifyFailureText(output);

        Assert.False(result.Success);
        Assert.Equal(expected, result.FailureReason);
    }

    [Fact]
    public void NonZeroExitCode_NeverThrowsAndAlwaysCarriesAMessage()
    {
        var outcome = new AstapPlateSolver.ProcessOutcome(1, "", "unexpected internal error 0xdeadbeef");

        PlateSolveResult result = AstapPlateSolver.MapProcessOutcome(outcome, "irrelevant.fits", TimeSpan.FromSeconds(1));

        Assert.False(result.Success);
        Assert.False(string.IsNullOrWhiteSpace(result.Message));
    }

    [Fact]
    public void BuildSolution_RecoversCenterFromWcsRegardlessOfCrpixPlacement()
    {
        // CRPIX is deliberately not the image center, to prove the adapter
        // asks the WCS for the true center rather than assuming CRVAL is it.
        const int width = 1000;
        const int height = 800;
        var wcs = new TanWcsSolution(
            Crpix1: 1.0,
            Crpix2: 1.0,
            Crval1Degrees: 200.0,
            Crval2Degrees: -10.0,
            Cd1_1: -0.0008333333333,
            Cd1_2: 0.0,
            Cd2_1: 0.0,
            Cd2_2: 0.0008333333333);

        PlateSolveSolution solution = AstapPlateSolver.BuildSolution(wcs, width, height, diagnosticOutput: "", elapsed: TimeSpan.FromSeconds(0.8));

        (double expectedRa, double expectedDec) = wcs.PixelToWorld(width / 2.0 + 0.5, height / 2.0 + 0.5);
        Assert.Equal(expectedRa, solution.CenterRaDegrees, precision: 9);
        Assert.Equal(expectedDec, solution.CenterDecDegrees, precision: 9);
    }

    [Fact]
    public void BuildSolution_RecoversPixelScaleFromCdMatrix()
    {
        var wcs = new TanWcsSolution(500, 400, 10.0, 20.0, Cd1_1: -0.00083333, Cd1_2: 0.0, Cd2_1: 0.0, Cd2_2: 0.00083333);

        PlateSolveSolution solution = AstapPlateSolver.BuildSolution(wcs, 1000, 800, "", TimeSpan.Zero);

        Assert.Equal(3.0, solution.PixelScaleArcsecPerPixel, precision: 3);
    }

    [Fact]
    public void BuildSolution_UnrotatedImage_ReportsZeroRotation()
    {
        var wcs = new TanWcsSolution(500, 400, 10.0, 20.0, Cd1_1: -0.0007, Cd1_2: 0.0, Cd2_1: 0.0, Cd2_2: 0.0007);

        PlateSolveSolution solution = AstapPlateSolver.BuildSolution(wcs, 1000, 800, "", TimeSpan.Zero);

        Assert.Equal(0.0, solution.RotationDegrees, precision: 6);
    }

    [Fact]
    public void BuildSolution_NinetyDegreeRotation_IsRecoveredFromCdMatrix()
    {
        // CD1_2 = -CDELT2*sin(90) = -CDELT2, CD2_2 = CDELT2*cos(90) = 0,
        // per the standard CD/CROTA2 relation documented on the adapter.
        const double cdelt2 = 0.0007;
        var wcs = new TanWcsSolution(500, 400, 10.0, 20.0, Cd1_1: 0.0, Cd1_2: -cdelt2, Cd2_1: -0.0007, Cd2_2: 0.0);

        PlateSolveSolution solution = AstapPlateSolver.BuildSolution(wcs, 1000, 800, "", TimeSpan.Zero);

        Assert.Equal(90.0, solution.RotationDegrees, precision: 6);
    }

    [Theory]
    [InlineData(-0.0007, 0.0, 0.0, 0.0007, -1)]
    [InlineData(0.0007, 0.0, 0.0, 0.0007, 1)]
    public void BuildSolution_PreservesCdMatrixDeterminantSign(double cd1_1, double cd1_2, double cd2_1, double cd2_2, int expectedSign)
    {
        var wcs = new TanWcsSolution(500, 400, 10.0, 20.0, cd1_1, cd1_2, cd2_1, cd2_2);

        PlateSolveSolution solution = AstapPlateSolver.BuildSolution(wcs, 1000, 800, "", TimeSpan.Zero);

        double determinant = solution.Cd1_1 * solution.Cd2_2 - solution.Cd1_2 * solution.Cd2_1;
        Assert.Equal(expectedSign, Math.Sign(determinant));
    }

    [Fact]
    public void SuccessfulExitCode_ButNoWcsWrittenBack_MapsToSolverError()
    {
        string path = Path.Combine(Path.GetTempPath(), $"fpa-astap-nowcs-{Guid.NewGuid():N}.fits");
        var image = new FitsImage(10, 10, FitsBitPix.Int16, bzero: 0.0, bscale: 1.0, new double[10, 10]);
        try
        {
            FitsFile.Write(path, image);

            var outcome = new AstapPlateSolver.ProcessOutcome(0, "Solved !", "");
            PlateSolveResult result = AstapPlateSolver.MapProcessOutcome(outcome, path, TimeSpan.FromSeconds(1));

            Assert.False(result.Success);
            Assert.Equal(PlateSolveFailureReason.SolverError, result.FailureReason);
        }
        finally
        {
            File.Delete(path);
        }
    }
}
