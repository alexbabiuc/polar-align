using System.Globalization;
using FreePolarAlign.Imaging.Fits;
using FreePolarAlign.Solving;
using Xunit;

namespace FreePolarAlign.Tests.Solving;

/// <summary>
/// ASPS's command line and result file as its vendor documents them
/// (astrogb.com, ASPS_CmdLine.pdf). ASPS is Windows-only, so nothing here runs
/// it; these pin down the translation in both directions.
/// </summary>
public class AspsPlateSolverTests
{
    private const string Image = "/tmp/solve/frame.fits";
    private const string Result = "/tmp/solve/frame.txt";

    private static FitsHeader Header(double? pixelMicrons = null, object? focalLength = null)
    {
        var header = new FitsHeader();
        if (pixelMicrons is { } pixel)
        {
            header.Set("XPIXSZ", pixel);
        }

        if (focalLength is not null)
        {
            header.Set("FOCALLEN", focalLength);
        }

        return header;
    }

    private static double Arg(string[] args, int index) => double.Parse(args[index], CultureInfo.InvariantCulture);

    /// <summary>Zeros throughout ask for a blind solve with ASPS's own optics settings.</summary>
    [Fact]
    public void NoHints_AsksForABlindSolveWithAspsSettings()
    {
        string[] args = AspsPlateSolver.BuildArguments(new PlateSolveRequest("in.fits"), Image, Result, Header());

        Assert.Equal(new[] { "/solvefile", Image, Result, "0", "0", "0", "0", "0" }, args);
    }

    [Fact]
    public void MeasuredScale_IsPassedAsFocalLengthForTheFramesPixelSize()
    {
        var request = new PlateSolveRequest("in.fits", ApproximateScaleArcsecPerPixel: 7.838);

        string[] args = AspsPlateSolver.BuildArguments(request, Image, Result, Header(pixelMicrons: 3.8));

        Assert.Equal(100.0, Arg(args, 3), precision: 1);
        Assert.Equal(3.8, Arg(args, 4), precision: 6);
    }

    /// <summary>A whole-number FOCALLEN is read back as an integer, and must still count.</summary>
    [Fact]
    public void NoMeasuredScale_UsesTheFramesOwnOptics()
    {
        string[] args = AspsPlateSolver.BuildArguments(
            new PlateSolveRequest("in.fits"), Image, Result, Header(pixelMicrons: 3.8, focalLength: 100));

        Assert.Equal(100.0, Arg(args, 3), precision: 6);
        Assert.Equal(3.8, Arg(args, 4), precision: 6);
    }

    [Fact]
    public void PositionHint_IsPassedInDegreesWithItsRadius()
    {
        var request = new PlateSolveRequest("in.fits", ApproximateRaDegrees: 43.99, ApproximateDecDegrees: 60.39, SearchRadiusDegrees: 5.0);

        string[] args = AspsPlateSolver.BuildArguments(request, Image, Result, Header());

        Assert.Equal(43.99, Arg(args, 5), precision: 6);
        Assert.Equal(60.39, Arg(args, 6), precision: 6);
        Assert.Equal(5.0, Arg(args, 7), precision: 6);
    }

    [Fact]
    public void ImagePath_IsPassedWithForwardSlashes()
    {
        string[] args = AspsPlateSolver.BuildArguments(
            new PlateSolveRequest("in.fits"), @"C:\Temp\solve\frame.fits", @"C:\Temp\solve\frame.txt", Header());

        Assert.Equal("C:/Temp/solve/frame.fits", args[1]);
        Assert.Equal(@"C:\Temp\solve\frame.txt", args[2]);
    }

    /// <summary>The successful output example from the vendor document, line for line.</summary>
    [Fact]
    public void DocumentedOkOutput_IsReadAsTheSolution()
    {
        string[] lines = { "OK", "43.9987030991", "60.3910687371", "210", "210", "6.16", "-0.63", "600" };

        PlateSolveResult result = AspsPlateSolver.ParseResult(lines, 2048, 2048, TimeSpan.FromSeconds(3));

        Assert.True(result.Success, result.Message);
        Assert.Equal(43.9987030991, result.Solution!.CenterRaDegrees, precision: 9);
        Assert.Equal(60.3910687371, result.Solution.CenterDecDegrees, precision: 9);
        Assert.Equal(6.16, result.Solution.PixelScaleArcsecPerPixel, precision: 9);
        Assert.Equal(-0.63, result.Solution.RotationDegrees, precision: 9);
    }

    [Theory]
    [InlineData("Cannot solve image file. Check the processing log file", PlateSolveFailureReason.NoMatchFound)]
    [InlineData("Star index files not found", PlateSolveFailureReason.SolverError)]
    [InlineData("Indexes non installed", PlateSolveFailureReason.SolverError)]
    [InlineData("File C:/Temp/frame.fits not found", PlateSolveFailureReason.InvalidImage)]
    [InlineData("Invalid FITS header", PlateSolveFailureReason.InvalidImage)]
    [InlineData("Astrometry.net library not installed", PlateSolveFailureReason.SolverError)]
    public void DocumentedErrors_MapToFailureReasons(string message, PlateSolveFailureReason expected)
    {
        PlateSolveResult result = AspsPlateSolver.ParseResult(new[] { "ERROR", message }, 100, 100, TimeSpan.Zero);

        Assert.Equal(expected, result.FailureReason);
        Assert.Contains(message, result.Message);
    }

    [Fact]
    public void OkWithMissingLines_IsASolverErrorNotACrash()
    {
        PlateSolveResult result = AspsPlateSolver.ParseResult(new[] { "OK", "43.99" }, 100, 100, TimeSpan.Zero);

        Assert.Equal(PlateSolveFailureReason.SolverError, result.FailureReason);
    }

    /// <summary>
    /// The CD matrix rebuilt from a scale and CROTA2 must be the one a real
    /// solver writes. These are the numbers ASTAP v2026.09.15 wrote for a
    /// simulator frame at 3.919 arcsec/pixel, CROTA2 -22.997 degrees. ASTAP's
    /// own matrix carries a slight skew (its CROTA1 is -23.002), which one
    /// rotation cannot reproduce, so agreement is to 1e-6 degrees: 0.004
    /// arcsec/pixel at worst.
    /// </summary>
    [Fact]
    public void RebuiltCdMatrix_MatchesWhatARealSolverWrote()
    {
        string[] lines = { "OK", "83.6", "22.0", "110", "82", "3.91914", "-22.99743666", "200" };

        PlateSolveSolution solution = AspsPlateSolver.ParseResult(lines, 1684, 1263, TimeSpan.Zero).Solution!;

        Assert.Equal(-1.002081979738E-003, solution.Cd1_1, precision: 6);
        Assert.Equal(4.254078095484E-004, solution.Cd1_2, precision: 6);
        Assert.Equal(4.253251655375E-004, solution.Cd2_1, precision: 6);
        Assert.Equal(1.002127948002E-003, solution.Cd2_2, precision: 6);
    }

    [Fact]
    public void ResolveExecutable_Folder_FindsPlateSolverExe()
    {
        string expected = Path.Combine(@"C:\PlateSolver", "PlateSolver.exe");

        string resolved = AspsPlateSolver.ResolveExecutable(
            @"C:\PlateSolver", path => path == expected, path => path == @"C:\PlateSolver", Array.Empty<string>());

        Assert.Equal(expected, resolved);
    }

    [Fact]
    public void ResolveExecutable_Empty_UsesTheVendorsInstallLocation()
    {
        const string installed = @"C:\Program Files (x86)\PlateSolver\PlateSolver.exe";

        string resolved = AspsPlateSolver.ResolveExecutable("", path => path == installed, _ => false, new[] { installed });

        Assert.Equal(installed, resolved);
    }

    [Fact]
    public async Task MissingExecutable_ReturnsSolverErrorNotException()
    {
        string imagePath = Path.Combine(Path.GetTempPath(), $"fpa-asps-missing-{Guid.NewGuid():N}.fits");
        FitsFile.Write(imagePath, new FitsImage(8, 8, FitsBitPix.Int16, 0.0, 1.0, new double[8, 8]));
        try
        {
            var solver = new AspsPlateSolver("/definitely/not/PlateSolver-" + Guid.NewGuid().ToString("N") + ".exe");

            PlateSolveResult result = await solver.SolveAsync(new PlateSolveRequest(imagePath));

            Assert.Equal(PlateSolveFailureReason.SolverError, result.FailureReason);
            Assert.Contains("ASPS", result.Message);
        }
        finally
        {
            File.Delete(imagePath);
        }
    }
}
