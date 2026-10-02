using System.Globalization;
using FreePolarAlign.Solving;
using Xunit;

namespace FreePolarAlign.Tests.Solving;

/// <summary>
/// Pure translation from the solver-agnostic request to ASTAP's CLI
/// arguments (see the remarks on <see cref="AstapPlateSolver"/> for which
/// flags and why). No process is started for any of these.
/// </summary>
public class AstapArgumentBuildingTests
{
    private const string ImagePath = "/tmp/staged.fits";

    /// <summary>
    /// Every search parameter is passed, so a blind solve does not inherit a
    /// field size or radius left in ASTAP's own settings by its GUI.
    /// </summary>
    [Fact]
    public void NoHints_AsksForAutomaticFieldAndWholeSkySearch()
    {
        var request = new PlateSolveRequest("original.fits");

        string[] args = AstapPlateSolver.BuildArguments(request, ImagePath, imageWidth: 1000, imageHeight: 1000);

        Assert.Equal(new[] { "-f", ImagePath, "-wcs", "-z", "0", "-fov", "0.0000", "-r", "180" }, args);
    }

    /// <summary>
    /// ASTAP's -fov is the field height, not the diagonal: given the diagonal
    /// for a 1684 x 1263 frame at 3.919 arcsec/pixel, ASTAP solved but warned
    /// "inexact scale! Set FOV=1.37d", which is the height.
    /// </summary>
    [Fact]
    public void ScaleHint_PassesFieldHeight()
    {
        var request = new PlateSolveRequest("original.fits", ApproximateScaleArcsecPerPixel: 3.919);

        string[] args = AstapPlateSolver.BuildArguments(request, ImagePath, imageWidth: 1684, imageHeight: 1263);

        int fovIndex = Array.IndexOf(args, "-fov");
        Assert.True(fovIndex >= 0);
        double actualFov = double.Parse(args[fovIndex + 1], CultureInfo.InvariantCulture);
        Assert.Equal(1.375, actualFov, precision: 3);
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

    [Fact]
    public void ResolveExecutable_Empty_UsesFirstInstalledDefault()
    {
        string resolved = AstapPlateSolver.ResolveExecutable(
            "", path => path == "/b/astap", _ => false, new[] { "/a/astap", "/b/astap" });

        Assert.Equal("/b/astap", resolved);
    }

    [Fact]
    public void ResolveExecutable_EmptyAndNothingInstalled_FallsBackToPath()
    {
        string resolved = AstapPlateSolver.ResolveExecutable(null, _ => false, _ => false, new[] { "/a/astap" });

        Assert.Equal("astap", resolved);
    }

    [Fact]
    public void ResolveExecutable_AppBundle_ReachesTheBinaryInside()
    {
        string resolved = AstapPlateSolver.ResolveExecutable(
            "/Applications/ASTAP.app", _ => false, path => path == "/Applications/ASTAP.app", Array.Empty<string>());

        Assert.Equal(Path.Combine("/Applications/ASTAP.app", "Contents", "MacOS", "astap"), resolved);
    }

    [Fact]
    public void ResolveExecutable_Folder_PrefersTheCommandLineBuild()
    {
        string cli = Path.Combine("/opt/astap", "astap_cli");
        string gui = Path.Combine("/opt/astap", "astap");

        string resolved = AstapPlateSolver.ResolveExecutable(
            "/opt/astap", path => path == cli || path == gui, path => path == "/opt/astap", Array.Empty<string>());

        Assert.Equal(cli, resolved);
    }

    [Fact]
    public void ResolveExecutable_FilePath_IsUsedAsGiven()
    {
        string resolved = AstapPlateSolver.ResolveExecutable(
            " /usr/bin/astap_cli ", _ => true, _ => false, new[] { "/a/astap" });

        Assert.Equal("/usr/bin/astap_cli", resolved);
    }
}
