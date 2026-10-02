using System.Globalization;
using FreePolarAlign.Imaging.Fits;
using FreePolarAlign.Solving;
using Xunit;

namespace FreePolarAlign.Tests.Solving;

/// <summary>
/// PlateSolve3's command line and result file as N.I.N.A. and CCDciel use them.
/// PlaneWave documents neither, and PlateSolve3 is Windows-only, so nothing here
/// runs it.
///
/// The result files below are constructed to that reading of the format, not
/// recorded from PlateSolve3: they pin down what this adapter does with such a
/// file, and the first night under the sky is what checks the reading itself.
/// </summary>
public class PlateSolve3SolverTests
{
    private const string Image = @"C:\Temp\solve\frame.fits";

    private static double Arg(string[] args, int index) => double.Parse(args[index], CultureInfo.InvariantCulture);

    [Fact]
    public void NoPositionHint_PassesOnlyTheImage()
    {
        string[] args = PlateSolve3Solver.BuildArguments(
            new PlateSolveRequest("in.fits", ApproximateScaleArcsecPerPixel: 3.9), Image, 1684, 1263, new FitsHeader());

        Assert.Equal(new[] { Image }, args);
    }

    /// <summary>PlateSolve3 cannot be given a centre without a field size.</summary>
    [Fact]
    public void PositionHintWithoutAnyScale_IsBlind()
    {
        var request = new PlateSolveRequest("in.fits", ApproximateRaDegrees: 56.75, ApproximateDecDegrees: 24.1);

        string[] args = PlateSolve3Solver.BuildArguments(request, Image, 1684, 1263, new FitsHeader());

        Assert.Equal(new[] { Image }, args);
    }

    [Fact]
    public void PositionAndScale_ArePassedInRadians()
    {
        var request = new PlateSolveRequest(
            "in.fits", ApproximateScaleArcsecPerPixel: 3.919, ApproximateRaDegrees: 56.75, ApproximateDecDegrees: 24.1);

        string[] args = PlateSolve3Solver.BuildArguments(request, Image, 1684, 1263, new FitsHeader());

        Assert.Equal(5, args.Length);
        Assert.Equal(56.75 * Math.PI / 180.0, Arg(args, 1), precision: 9);
        Assert.Equal(24.1 * Math.PI / 180.0, Arg(args, 2), precision: 9);
        Assert.Equal(1684 * 3.919 / 206264.806, Arg(args, 3), precision: 9);
        Assert.Equal(1263 * 3.919 / 206264.806, Arg(args, 4), precision: 9);
    }

    [Fact]
    public void PositionHintWithFrameOptics_UsesThemForTheFieldSize()
    {
        var header = new FitsHeader();
        header.Set("FOCALLEN", 200);
        header.Set("XPIXSZ", 3.8);
        var request = new PlateSolveRequest("in.fits", ApproximateRaDegrees: 56.75, ApproximateDecDegrees: 24.1);

        string[] args = PlateSolve3Solver.BuildArguments(request, Image, 1684, 1263, header);

        double scale = 206.264806 * 3.8 / 200.0;
        Assert.Equal(1263 * scale / 206264.806, Arg(args, 4), precision: 9);
    }

    [Fact]
    public void ResultFile_IsBesideTheImageWithPs3Suffix()
    {
        string path = PlateSolve3Solver.ResultPath(Path.Combine("solve", "frame.fits"));

        Assert.Equal(Path.Combine("solve", "frame_PS3.txt"), path);
    }

    /// <summary>
    /// RA 83.6, Dec 22.0 at 3.919 arcsec/pixel and CROTA2 -22.997: the frame
    /// ASTAP solved for real, so the CD matrix to expect is the one it wrote.
    /// PlateSolve3's rotation is 180 - CROTA2, and A to D carry the CD signs
    /// negated (CCDciel).
    /// </summary>
    private static readonly string[] Solved =
    {
        "True",
        "1.4590952,0.3839724",
        "52630.4296,202.9974366",
        "Kepler",
        "0.95,-0.39,-0.39,-0.95,0,0,0,0",
        "0,0",
        "0,0.5,100,150,120",
    };

    [Fact]
    public void SolvedResult_IsReadInDegreesAndArcsecondsPerPixel()
    {
        PlateSolveResult result = PlateSolve3Solver.ParseResult(Solved, 1684, 1263, TimeSpan.FromSeconds(2));

        Assert.True(result.Success, result.Message);
        Assert.Equal(83.6, result.Solution!.CenterRaDegrees, precision: 4);
        Assert.Equal(22.0, result.Solution.CenterDecDegrees, precision: 4);
        Assert.Equal(3.919, result.Solution.PixelScaleArcsecPerPixel, precision: 3);
    }

    [Fact]
    public void SolvedResult_RebuildsTheCdMatrixARealSolverWrote()
    {
        PlateSolveSolution solution = PlateSolve3Solver.ParseResult(Solved, 1684, 1263, TimeSpan.Zero).Solution!;

        Assert.Equal(-1.002081979738E-003, solution.Cd1_1, precision: 6);
        Assert.Equal(4.254078095484E-004, solution.Cd1_2, precision: 6);
        Assert.Equal(4.253251655375E-004, solution.Cd2_1, precision: 6);
        Assert.Equal(1.002127948002E-003, solution.Cd2_2, precision: 6);
    }

    /// <summary>A mirrored transform must come out mirrored: D12 keys off that sign.</summary>
    [Fact]
    public void MirroredTransform_GivesAPositiveDeterminant()
    {
        string[] mirrored = (string[])Solved.Clone();
        mirrored[4] = "-0.95,-0.39,0.39,-0.95,0,0,0,0";

        PlateSolveSolution solution = PlateSolve3Solver.ParseResult(mirrored, 1684, 1263, TimeSpan.Zero).Solution!;
        PlateSolveSolution plain = PlateSolve3Solver.ParseResult(Solved, 1684, 1263, TimeSpan.Zero).Solution!;

        Assert.True(solution.Cd1_1 * solution.Cd2_2 - solution.Cd1_2 * solution.Cd2_1 > 0.0);
        Assert.True(plain.Cd1_1 * plain.Cd2_2 - plain.Cd1_2 * plain.Cd2_1 < 0.0);
    }

    /// <summary>CCDciel found PlateSolve3 writing decimal commas on some locales.</summary>
    [Fact]
    public void DecimalCommaResult_ReadsTheSameAsDecimalPoint()
    {
        string[] comma =
        {
            "True",
            "1,4590952,0,3839724",
            "52630,4296,202,9974366",
            "Kepler",
            "0,95,-0,39,-0,39,-0,95,0,0,0,0,0,0,0,0",
        };

        PlateSolveSolution fromComma = PlateSolve3Solver.ParseResult(comma, 1684, 1263, TimeSpan.Zero).Solution!;
        PlateSolveSolution fromPoint = PlateSolve3Solver.ParseResult(Solved, 1684, 1263, TimeSpan.Zero).Solution!;

        Assert.Equal(fromPoint.CenterRaDegrees, fromComma.CenterRaDegrees, precision: 9);
        Assert.Equal(fromPoint.CenterDecDegrees, fromComma.CenterDecDegrees, precision: 9);
        Assert.Equal(fromPoint.PixelScaleArcsecPerPixel, fromComma.PixelScaleArcsecPerPixel, precision: 9);
        Assert.Equal(fromPoint.Cd1_2, fromComma.Cd1_2, precision: 12);
    }

    [Fact]
    public void FalseResult_IsNoMatch()
    {
        PlateSolveResult result = PlateSolve3Solver.ParseResult(new[] { "False" }, 100, 100, TimeSpan.Zero);

        Assert.Equal(PlateSolveFailureReason.NoMatchFound, result.FailureReason);
    }

    [Fact]
    public void TrueWithGarbledNumbers_IsASolverErrorNotACrash()
    {
        PlateSolveResult result = PlateSolve3Solver.ParseResult(new[] { "True", "x,y", "z" }, 100, 100, TimeSpan.Zero);

        Assert.Equal(PlateSolveFailureReason.SolverError, result.FailureReason);
    }

    [Fact]
    public void ResolveExecutable_Folder_FindsThePlateSolve3Executable()
    {
        string expected = Path.Combine(@"C:\PS3", "PlateSolve3.80.exe");

        string resolved = PlateSolve3Solver.ResolveExecutable(
            @"C:\PS3",
            _ => false,
            path => path == @"C:\PS3",
            _ => new[] { Path.Combine(@"C:\PS3", "ps3cli.exe"), expected },
            Array.Empty<string>());

        Assert.Equal(expected, resolved);
    }

    [Fact]
    public async Task MissingExecutable_ReturnsSolverErrorNotException()
    {
        string imagePath = Path.Combine(Path.GetTempPath(), $"fpa-ps3-missing-{Guid.NewGuid():N}.fits");
        FitsFile.Write(imagePath, new FitsImage(8, 8, FitsBitPix.Int16, 0.0, 1.0, new double[8, 8]));
        try
        {
            var solver = new PlateSolve3Solver("/definitely/not/PlateSolve3-" + Guid.NewGuid().ToString("N") + ".exe");

            PlateSolveResult result = await solver.SolveAsync(new PlateSolveRequest(imagePath));

            Assert.Equal(PlateSolveFailureReason.SolverError, result.FailureReason);
            Assert.Contains("PlateSolve3", result.Message);
        }
        finally
        {
            File.Delete(imagePath);
        }
    }
}
