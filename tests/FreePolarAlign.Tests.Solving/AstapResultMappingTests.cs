using FreePolarAlign.Imaging.Wcs;
using FreePolarAlign.Solving;
using Xunit;

namespace FreePolarAlign.Tests.Solving;

/// <summary>
/// ASTAP's exit code, its .ini result file and its console text are the only
/// signals this adapter gets about what happened, so the mapping from them to
/// <see cref="PlateSolveResult"/> is tested directly, without starting any
/// process.
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
        var outcome = new AstapPlateSolver.ProcessOutcome(99, "", "unexpected internal error 0xdeadbeef");

        PlateSolveResult result = AstapPlateSolver.MapProcessOutcome(outcome, NoSuchIni(), 1000, 800, TimeSpan.FromSeconds(1));

        Assert.False(result.Success);
        Assert.False(string.IsNullOrWhiteSpace(result.Message));
    }

    /// <summary>ASTAP's documented exit codes, which say more than its wording does.</summary>
    [Theory]
    [InlineData(1, PlateSolveFailureReason.NoMatchFound)]
    [InlineData(2, PlateSolveFailureReason.NoStarsDetected)]
    [InlineData(16, PlateSolveFailureReason.InvalidImage)]
    [InlineData(32, PlateSolveFailureReason.SolverError)]
    [InlineData(33, PlateSolveFailureReason.SolverError)]
    public void ExitCode_MapsToFailureReason(int exitCode, PlateSolveFailureReason expected)
    {
        PlateSolveResult result = AstapPlateSolver.ClassifyExitCode(exitCode, "");

        Assert.Equal(expected, result.FailureReason);
    }

    /// <summary>
    /// The defect this adapter shipped with: it read the solution back from the
    /// FITS header, where ASTAP never writes it. The .ini below is what ASTAP
    /// v2026.09.15 wrote for a simulator frame centred at RA 83.6, Dec 22.0,
    /// 1684 x 1263 pixels at 3.919 arcsec/pixel, rotated 23 degrees.
    /// </summary>
    [Fact]
    public void SolvedIni_IsReadBackAsTheSolution()
    {
        string ini = WriteIni(
            "PLTSOLVD=T",
            "CRPIX1= 8.4250000000000000E+002",
            "CRPIX2= 6.3200000000000000E+002",
            "CRVAL1= 8.3600020957526084E+001",
            "CRVAL2= 2.1999990194922525E+001",
            "CDELT1=-1.0886414003428448E-003",
            "CDELT2= 1.0886514229114042E-003",
            "CROTA1=-2.3002385773952547E+001",
            "CROTA2=-2.2997436661759306E+001",
            "CD1_1=-1.0020819797380185E-003",
            "CD1_2= 4.2540780954839516E-004",
            "CD2_1= 4.2532516553754347E-004",
            "CD2_2= 1.0021279480023927E-003",
            "DIMENSIONS=1684 x 1263");
        try
        {
            var outcome = new AstapPlateSolver.ProcessOutcome(0, "Solution found:  5: 34  24.01\t+22° 00  00.0", "");

            PlateSolveResult result = AstapPlateSolver.MapProcessOutcome(outcome, ini, 1684, 1263, TimeSpan.FromSeconds(4.3));

            Assert.True(result.Success, result.Message);
            Assert.Equal(83.6, result.Solution!.CenterRaDegrees, precision: 3);
            Assert.Equal(22.0, result.Solution.CenterDecDegrees, precision: 3);
            Assert.Equal(3.919, result.Solution.PixelScaleArcsecPerPixel, precision: 2);
        }
        finally
        {
            File.Delete(ini);
        }
    }

    /// <summary>What ASTAP wrote for a frame of pure noise, exiting with 2.</summary>
    [Fact]
    public void FailedIni_CarriesAstapsOwnExplanation()
    {
        string ini = WriteIni("PLTSOLVD=F", "DIMENSIONS=800 x 600", "ERROR=Not enough stars.", "WARNING=Warning, small image dimensions! ");
        try
        {
            var outcome = new AstapPlateSolver.ProcessOutcome(2, "Only 0 stars found in image. Abort\nNo solution found!  :(", "");

            PlateSolveResult result = AstapPlateSolver.MapProcessOutcome(outcome, ini, 800, 600, TimeSpan.FromSeconds(1));

            Assert.Equal(PlateSolveFailureReason.NoStarsDetected, result.FailureReason);
            Assert.Contains("Not enough stars", result.Message);
        }
        finally
        {
            File.Delete(ini);
        }
    }

    [Fact]
    public void SolvedIni_MissingCdMatrix_MapsToSolverError()
    {
        string ini = WriteIni("PLTSOLVD=T", "CRPIX1=1", "CRPIX2=1", "CRVAL1=10", "CRVAL2=20");
        try
        {
            PlateSolveResult result = AstapPlateSolver.MapProcessOutcome(
                new AstapPlateSolver.ProcessOutcome(0, "", ""), ini, 100, 100, TimeSpan.Zero);

            Assert.Equal(PlateSolveFailureReason.SolverError, result.FailureReason);
        }
        finally
        {
            File.Delete(ini);
        }
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
    public void SuccessfulExitCode_ButNoResultFile_MapsToSolverError()
    {
        var outcome = new AstapPlateSolver.ProcessOutcome(0, "Solved !", "");

        PlateSolveResult result = AstapPlateSolver.MapProcessOutcome(outcome, NoSuchIni(), 10, 10, TimeSpan.FromSeconds(1));

        Assert.False(result.Success);
        Assert.Equal(PlateSolveFailureReason.SolverError, result.FailureReason);
    }

    private static string NoSuchIni() => Path.Combine(Path.GetTempPath(), $"fpa-astap-none-{Guid.NewGuid():N}.ini");

    private static string WriteIni(params string[] lines)
    {
        string path = Path.Combine(Path.GetTempPath(), $"fpa-astap-ini-{Guid.NewGuid():N}.ini");
        File.WriteAllLines(path, lines);
        return path;
    }
}
