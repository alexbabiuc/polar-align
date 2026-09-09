using System.Globalization;
using FreePolarAlign.Solving;
using Xunit;

namespace FreePolarAlign.Tests.Solving;

/// <summary>
/// Pure translation from the solver-agnostic request to ASTAP's documented
/// CLI arguments (see the remarks on <see cref="AstapPlateSolver"/> for which
/// flags and why). No process is started for any of these.
/// </summary>
public class AstapArgumentBuildingTests
{
    private const string ImagePath = "/tmp/staged.fits";

    [Fact]
    public void NoHints_PassesOnlyFileAndWcsFlags()
    {
        var request = new PlateSolveRequest("original.fits");

        string[] args = AstapPlateSolver.BuildArguments(request, ImagePath, imageWidth: 1000, imageHeight: 1000);

        Assert.Equal(new[] { "-f", ImagePath, "-wcs", "-z", "0" }, args);
    }

    [Fact]
    public void ScaleHint_AddsFieldOfViewFlag()
    {
        var request = new PlateSolveRequest("original.fits", ApproximateScaleArcsecPerPixel: 3.0);
        double diagonalPixels = Math.Sqrt(1200.0 * 1200.0 + 900.0 * 900.0);
        double expectedFov = diagonalPixels * 3.0 / 3600.0;

        string[] args = AstapPlateSolver.BuildArguments(request, ImagePath, imageWidth: 1200, imageHeight: 900);

        int fovIndex = Array.IndexOf(args, "-fov");
        Assert.True(fovIndex >= 0);
        double actualFov = double.Parse(args[fovIndex + 1], CultureInfo.InvariantCulture);
        Assert.Equal(expectedFov, actualFov, precision: 4);
    }

    [Fact]
    public void PositionHint_AddsRaSpdAndRadiusFlags()
    {
        var request = new PlateSolveRequest("original.fits", ApproximateRaDegrees: 90.0, ApproximateDecDegrees: -30.0, SearchRadiusDegrees: 6.0);

        string[] args = AstapPlateSolver.BuildArguments(request, ImagePath, imageWidth: 1000, imageHeight: 1000);

        // ASTAP wants RA in decimal hours, and "south polar distance" (dec + 90), not dec directly.
        int raIndex = Array.IndexOf(args, "-ra");
        int spdIndex = Array.IndexOf(args, "-spd");
        int rIndex = Array.IndexOf(args, "-r");
        Assert.True(raIndex >= 0 && spdIndex >= 0 && rIndex >= 0);
        Assert.Equal(6.0, double.Parse(args[raIndex + 1], CultureInfo.InvariantCulture), precision: 6);
        Assert.Equal(60.0, double.Parse(args[spdIndex + 1], CultureInfo.InvariantCulture), precision: 6);
        Assert.Equal(6.0, double.Parse(args[rIndex + 1], CultureInfo.InvariantCulture), precision: 6);
    }

    [Fact]
    public void PositionHint_WithoutExplicitSearchRadius_DefaultsToTenDegrees()
    {
        var request = new PlateSolveRequest("original.fits", ApproximateRaDegrees: 10.0, ApproximateDecDegrees: 10.0);

        string[] args = AstapPlateSolver.BuildArguments(request, ImagePath, imageWidth: 1000, imageHeight: 1000);

        int rIndex = Array.IndexOf(args, "-r");
        Assert.Equal(10.0, double.Parse(args[rIndex + 1], CultureInfo.InvariantCulture), precision: 6);
    }

    [Fact]
    public void PartialPositionHint_OmitsRaSpdFlags()
    {
        var request = new PlateSolveRequest("original.fits", ApproximateRaDegrees: 10.0);

        string[] args = AstapPlateSolver.BuildArguments(request, ImagePath, imageWidth: 1000, imageHeight: 1000);

        Assert.DoesNotContain("-ra", args);
        Assert.DoesNotContain("-spd", args);
    }
}
