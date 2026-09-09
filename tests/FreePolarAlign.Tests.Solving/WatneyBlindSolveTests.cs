using System.Net.Http;
using FreePolarAlign.Imaging.Fits;
using FreePolarAlign.Imaging.Wcs;
using FreePolarAlign.Solving;
using Xunit;

namespace FreePolarAlign.Tests.Solving;

/// <summary>
/// Proves an actual blind solve works end to end -- not just the request
/// translation, which the rest of this project covers without a real solve.
/// Uses real Tycho-2 stars (fetched from VizieR) painted onto a frame at a
/// WCS this test picks, so Watney's answer can be checked against an exactly
/// known truth. Requires both network access and the downloaded 00-07 quad
/// database pack (D13); skips cleanly, rather than failing, when either is
/// unavailable, so a fresh clone's `dotnet test` still passes.
/// </summary>
[Trait("Category", "RequiresNetwork")]
[Trait("Category", "RequiresQuadDatabase")]
public class WatneyBlindSolveTests
{
    /// <summary>
    /// Default matches the path this project downloads and extracts the
    /// D13 00-07 quad-database pack into; overridable so CI or another
    /// machine can point at a different location.
    /// </summary>
    private static string QuadDatabaseDirectory =>
        Environment.GetEnvironmentVariable("FPA_QUADDB_DIR")
        ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".free-polar-align", "quaddb");

    [SkippableFact]
    public async Task BlindSolve_OnRealPleiadesStarField_RecoversKnownFieldCenter()
    {
        string quadDbDir = QuadDatabaseDirectory;
        Skip.IfNot(Directory.Exists(quadDbDir) && Directory.EnumerateFiles(quadDbDir, "*.qdb").Any(),
            $"No Watney quad database found at '{quadDbDir}' (set FPA_QUADDB_DIR, or download watneyqdb-00-07-20-v3 per D13). Skipping the real-solve test.");

        // The Pleiades (M45): real, bright, well populated, and comfortably
        // inside the 00-07 pack's >= 0.8 degree field-radius coverage. Field
        // size and star count were tuned empirically: a smaller field with
        // only ~40 detected stars was too sparse for Watney's minimum of 5
        // matching quads, even though it read the database and searched
        // correctly; ~150 detected stars solves reliably in well under a second.
        const double centerRa = 56.75;
        const double centerDec = 24.10;
        const int width = 1600;
        const int height = 1200;
        const double scaleArcsecPerPixel = 5.0;

        IReadOnlyList<RealSkyFitsFixture.CatalogStar> stars;
        try
        {
            stars = await RealSkyFitsFixture.FetchTycho2StarsAsync(centerRa, centerDec, radiusDegrees: 1.6);
        }
        catch (HttpRequestException ex)
        {
            Skip.If(true, $"Could not reach VizieR to fetch real catalog stars: {ex.Message}. Skipping the real-solve test.");
            return;
        }
        catch (TaskCanceledException ex)
        {
            Skip.If(true, $"Timed out reaching VizieR: {ex.Message}. Skipping the real-solve test.");
            return;
        }

        Skip.If(stars.Count < 60, $"VizieR returned only {stars.Count} usable stars, too few for a reliable solve. Skipping.");

        double scaleDegPerPixel = scaleArcsecPerPixel / 3600.0;
        var wcs = new TanWcsSolution(
            Crpix1: width / 2.0 + 0.5,
            Crpix2: height / 2.0 + 0.5,
            Crval1Degrees: centerRa,
            Crval2Degrees: centerDec,
            Cd1_1: -scaleDegPerPixel,
            Cd1_2: 0.0,
            Cd2_1: 0.0,
            Cd2_2: scaleDegPerPixel);

        FitsImage image = RealSkyFitsFixture.RenderStarField(stars, width, height, wcs);

        string imagePath = Path.Combine(Path.GetTempPath(), $"fpa-pleiades-{Guid.NewGuid():N}.fits");
        FitsFile.Write(imagePath, image);

        try
        {
            using var solver = new WatneyPlateSolver(quadDbDir);
            var request = new PlateSolveRequest(
                ImagePath: imagePath,
                ApproximateScaleArcsecPerPixel: scaleArcsecPerPixel,
                ScaleToleranceFraction: 0.2,
                Timeout: TimeSpan.FromMinutes(2));

            PlateSolveResult result = await solver.SolveAsync(request);

            Assert.True(result.Success, $"Expected a successful blind solve, got: {result.FailureReason} - {result.Message}");
            PlateSolveSolution solution = result.Solution!;

            double separationArcmin = AngularSeparationArcmin(centerRa, centerDec, solution.CenterRaDegrees, solution.CenterDecDegrees);

            Assert.True(separationArcmin < 5.0,
                $"Solved center ({solution.CenterRaDegrees:F5}, {solution.CenterDecDegrees:F5}) is {separationArcmin:F2}' " +
                $"from the known field center ({centerRa}, {centerDec}), expected < 5'.");

            Assert.True(solution.MatchedStarCount > 0);

            // D12's parity logic reads the CD determinant's sign; the frame
            // was built with a normal-parity CD matrix (Cd1_1 < 0, Cd2_2 > 0,
            // no cross terms), so the recovered determinant must stay negative.
            double determinant = solution.Cd1_1 * solution.Cd2_2 - solution.Cd1_2 * solution.Cd2_1;
            Assert.True(determinant < 0, $"Expected a negative CD determinant (normal parity), got {determinant}.");
        }
        finally
        {
            File.Delete(imagePath);
        }
    }

    private static double AngularSeparationArcmin(double ra1Deg, double dec1Deg, double ra2Deg, double dec2Deg)
    {
        const double degToRad = Math.PI / 180.0;
        double ra1 = ra1Deg * degToRad, dec1 = dec1Deg * degToRad;
        double ra2 = ra2Deg * degToRad, dec2 = dec2Deg * degToRad;

        double cosSeparation = Math.Sin(dec1) * Math.Sin(dec2) + Math.Cos(dec1) * Math.Cos(dec2) * Math.Cos(ra1 - ra2);
        cosSeparation = Math.Clamp(cosSeparation, -1.0, 1.0);
        double separationRad = Math.Acos(cosSeparation);
        return separationRad * (180.0 / Math.PI) * 60.0;
    }
}
