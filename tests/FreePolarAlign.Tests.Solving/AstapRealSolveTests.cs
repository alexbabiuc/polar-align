using FreePolarAlign.Devices.Simulated.SyntheticSky;
using FreePolarAlign.Imaging.Fits;
using FreePolarAlign.Imaging.Wcs;
using FreePolarAlign.Solving;
using Xunit;

namespace FreePolarAlign.Tests.Solving;

/// <summary>
/// The adapter against a real ASTAP, on simulator frames whose true WCS is
/// known exactly. Everything else about ASTAP in this project is tested against
/// recorded output; this is what keeps that recording honest when ASTAP
/// changes.
///
/// Needs ASTAP and a star database covering the field (D05 or denser for these
/// 2.3 degree frames), so it skips on a machine without them. FPA_ASTAP
/// overrides where ASTAP is looked for.
/// </summary>
[Trait("Category", "RequiresAstap")]
public class AstapRealSolveTests
{
    private const int Width = 1684;
    private const int Height = 1263;
    private const double PitchMicrons = 3.8;
    private const double FocalLengthMm = 200.0;
    private const double RotationDegrees = 23.0;

    private static StarCatalog? _catalog;

    private static StarCatalog Catalog => _catalog ??= StarCatalog.LoadCsv(
        Path.Combine(AppContext.BaseDirectory, "fixtures", "tycho2_subset.csv"));

    private static string? InstalledAstap()
    {
        string executable = AstapPlateSolver.ResolveExecutable(Environment.GetEnvironmentVariable("FPA_ASTAP"));
        return File.Exists(executable) ? executable : null;
    }

    /// <summary>
    /// The committed catalogue's rich, medium and sparse fields, and one in the
    /// Dec 62-78 band the simulated mount is pointed into, which is limited to
    /// VTmag 11 and so is the thinnest field a session renders.
    /// </summary>
    [SkippableTheory]
    [InlineData(300.0, 35.0)]
    [InlineData(83.6, 22.0)]
    [InlineData(192.86, 27.13)]
    [InlineData(120.0, 70.0)]
    public async Task BlindSolve_RecoversTheTrueWcs(double raDegrees, double decDegrees)
    {
        string? astap = InstalledAstap();
        Skip.If(astap is null, "ASTAP is not installed where it is looked for; set FPA_ASTAP to run this.");

        (string path, TanWcsSolution truth) = RenderFrame(raDegrees, decDegrees);
        try
        {
            PlateSolveResult result = await new AstapPlateSolver(astap!).SolveAsync(
                new PlateSolveRequest(path, Timeout: TimeSpan.FromSeconds(60)));

            SkipIfNoDatabase(result);
            AssertMatches(truth, result);
        }
        finally
        {
            File.Delete(path);
        }
    }

    /// <summary>
    /// A near solve as the session issues one: the mount's belief a few degrees
    /// out, and a measured scale.
    /// </summary>
    [SkippableFact]
    public async Task NearSolve_WithPositionAndScaleHints_RecoversTheTrueWcs()
    {
        string? astap = InstalledAstap();
        Skip.If(astap is null, "ASTAP is not installed where it is looked for; set FPA_ASTAP to run this.");

        (string path, TanWcsSolution truth) = RenderFrame(83.6, 22.0);
        try
        {
            var request = new PlateSolveRequest(
                path,
                ApproximateScaleArcsecPerPixel: truth.PixelScaleArcsecondsPerPixel,
                ScaleToleranceFraction: 0.05,
                ApproximateRaDegrees: 86.0,
                ApproximateDecDegrees: 20.5,
                SearchRadiusDegrees: 10.0,
                Timeout: TimeSpan.FromSeconds(60));

            PlateSolveResult result = await new AstapPlateSolver(astap!).SolveAsync(request);

            SkipIfNoDatabase(result);
            AssertMatches(truth, result);
        }
        finally
        {
            File.Delete(path);
        }
    }

    /// <summary>
    /// With no database ASTAP's GUI build stops at a dialog, which the adapter
    /// sees as a timeout. That is a missing install, not a defect here.
    /// </summary>
    private static void SkipIfNoDatabase(PlateSolveResult result) =>
        Skip.If(
            result.FailureReason is PlateSolveFailureReason.Timeout ||
            (result.FailureReason is PlateSolveFailureReason.SolverError && result.Message!.Contains("star database")),
            $"ASTAP ran but could not use a star database: {result.Message}");

    private static void AssertMatches(TanWcsSolution truth, PlateSolveResult result)
    {
        Assert.True(result.Success, result.Message);
        PlateSolveSolution solution = result.Solution!;

        (double trueRa, double trueDec) = truth.PixelToWorld(Width / 2.0 + 0.5, Height / 2.0 + 0.5);
        double separationArcsec = AngularSeparationDegrees(trueRa, trueDec, solution.CenterRaDegrees, solution.CenterDecDegrees) * 3600.0;
        Assert.True(separationArcsec < 10.0, $"centre {separationArcsec:F1} arcsec from the truth");

        Assert.Equal(truth.PixelScaleArcsecondsPerPixel, solution.PixelScaleArcsecPerPixel, precision: 2);

        // The CD matrix element by element: orientation and parity both, which
        // is what D12's correction directions are derived from.
        Assert.Equal(truth.Cd1_1, solution.Cd1_1, precision: 5);
        Assert.Equal(truth.Cd1_2, solution.Cd1_2, precision: 5);
        Assert.Equal(truth.Cd2_1, solution.Cd2_1, precision: 5);
        Assert.Equal(truth.Cd2_2, solution.Cd2_2, precision: 5);
    }

    /// <summary>A frame with no WCS in it, so the solve really is a solve.</summary>
    private static (string Path, TanWcsSolution Truth) RenderFrame(double raDegrees, double decDegrees)
    {
        TanWcsSolution truth = SkyRenderer.BuildWcs(raDegrees, decDegrees, Width, Height, PitchMicrons, FocalLengthMm, RotationDegrees);
        FitsImage rendered = SkyRenderer.Render(Catalog, truth, Width, Height, ObservingConditions.Nominal, new Random(1));

        var header = new FitsHeader();
        header.Set("EXPTIME", ObservingConditions.Nominal.ExposureSeconds);
        var bare = new FitsImage(rendered.Width, rendered.Height, rendered.BitPix, rendered.Bzero, rendered.Bscale, rendered.Pixels, header);

        string path = Path.Combine(Path.GetTempPath(), $"fpa-astap-real-{Guid.NewGuid():N}.fits");
        FitsFile.Write(path, bare);
        return (path, truth);
    }

    private static double AngularSeparationDegrees(double ra1, double dec1, double ra2, double dec2)
    {
        double toRad = Math.PI / 180.0;
        double cos = Math.Sin(dec1 * toRad) * Math.Sin(dec2 * toRad) +
                     Math.Cos(dec1 * toRad) * Math.Cos(dec2 * toRad) * Math.Cos((ra1 - ra2) * toRad);
        return Math.Acos(Math.Clamp(cos, -1.0, 1.0)) / toRad;
    }
}
