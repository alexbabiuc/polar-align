using FreePolarAlign.Core.Alignment;
using FreePolarAlign.Core.Astrometry;
using FreePolarAlign.Devices.Simulated.SyntheticSky;
using FreePolarAlign.Imaging.Detection;
using FreePolarAlign.Imaging.Fits;
using FreePolarAlign.Imaging.Wcs;
using FreePolarAlign.Solving;
using Xunit;

namespace FreePolarAlign.Tests.EndToEnd;

/// <summary>
/// Phase 2 exit criterion, run through the whole pipeline: render a field from a
/// real catalogue, solve it blind, recover the focal length, and feed a sequence
/// of solves into the Phase 1 fit to recover an injected misalignment.
///
/// Nothing here is stubbed. The frames are written as real FITS with the true
/// WCS stripped out, so the solver has no way to cheat, and the solve is the
/// same embedded Watney the product will ship. This is what the virtual
/// observatory exists for: a closed loop with exact ground truth that runs on a
/// laptop at noon.
/// </summary>
public class Phase2ExitCriterionTests
{
    private static readonly string QuadDatabaseDirectory =
        Environment.GetEnvironmentVariable("FPA_QUADDB_DIR")
        ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".free-polar-align", "quaddb");

    private static StarCatalog? _catalog;

    private static StarCatalog Catalog => _catalog ??= StarCatalog.LoadCsv(
        Path.Combine(AppContext.BaseDirectory, "fixtures", "tycho2_subset.csv"));

    private static void RequireQuadDatabase()
    {
        Skip.IfNot(
            Directory.Exists(QuadDatabaseDirectory) && Directory.EnumerateFiles(QuadDatabaseDirectory, "*.qdb").Any(),
            $"No Watney quad database at '{QuadDatabaseDirectory}'. Set FPA_QUADDB_DIR, or download " +
            "watneyqdb-00-07-20-v3 per D13. Skipping the end-to-end solve.");
    }

    /// <summary>
    /// Writes a rendered frame to disk with every WCS keyword removed, so a
    /// "blind" solve genuinely is one. Without this the test would pass by
    /// handing the answer to the solver in the header.
    /// </summary>
    private static string WriteFrameWithoutWcs(FitsImage image)
    {
        var stripped = new FitsHeader();
        foreach (FitsCard card in image.ExtraHeader.Cards)
        {
            bool isWcs = card.Keyword.StartsWith("CTYPE", StringComparison.Ordinal)
                      || card.Keyword.StartsWith("CRPIX", StringComparison.Ordinal)
                      || card.Keyword.StartsWith("CRVAL", StringComparison.Ordinal)
                      || card.Keyword.StartsWith("CUNIT", StringComparison.Ordinal)
                      || card.Keyword.StartsWith("CD1_", StringComparison.Ordinal)
                      || card.Keyword.StartsWith("CD2_", StringComparison.Ordinal)
                      || card.Keyword.StartsWith("CDELT", StringComparison.Ordinal)
                      || card.Keyword.StartsWith("CROTA", StringComparison.Ordinal);
            if (!isWcs && card.Value is not null)
            {
                stripped.Set(card.Keyword, card.Value, card.Comment);
            }
        }

        var blind = new FitsImage(image.Width, image.Height, image.BitPix, image.Bzero, image.Bscale, image.Pixels, stripped);
        string path = Path.Combine(Path.GetTempPath(), $"fpa-e2e-{Guid.NewGuid():N}.fits");
        FitsFile.Write(path, blind);
        return path;
    }

    /// <summary>
    /// Blind solve plus focal length recovery to better than 0.5%, across the
    /// part of D13's envelope the bundled index packs cover.
    ///
    /// Deliberately not the whole envelope. The bundled packs (O5) cover a field
    /// radius of 0.6 degrees and wider, so the 0.6 degree *diagonal* end of D13
    /// needs the optional 10-11 and 12-13 downloads -- and Tycho-2 is too
    /// shallow there anyway, yielding around a dozen stars. That corner is
    /// covered by <see cref="NarrowField_FailsCleanlyWithoutItsIndexPack"/>
    /// instead, which is the honest test given what ships.
    /// </summary>
    [SkippableTheory]
    [InlineData("7.4deg rich", 100.0, 2737, 2053, 300.0, 35.0)]
    [InlineData("7.4deg medium density", 100.0, 2737, 2053, 83.6, 22.0)]
    [InlineData("2.3deg", 200.0, 1684, 1263, 300.0, 35.0)]
    [InlineData("1.6deg", 300.0, 1684, 1263, 300.0, 35.0)]
    [InlineData("1.2deg", 400.0, 1684, 1263, 300.0, 35.0)]
    public void RenderedField_SolvesBlind_AndRecoversFocalLength(
        string label, double focalLengthMm, int width, int height, double raDegrees, double decDegrees)
    {
        RequireQuadDatabase();

        const double pitchMicrons = 3.8;
        TanWcsSolution truth = SkyRenderer.BuildWcs(
            raDegrees, decDegrees, width, height, pitchMicrons, focalLengthMm, rotationDegrees: 23.0);

        FitsImage frame = SkyRenderer.Render(Catalog, truth, width, height, ObservingConditions.Nominal, new Random(101));
        string path = WriteFrameWithoutWcs(frame);

        try
        {
            using var solver = new WatneyPlateSolver(QuadDatabaseDirectory);
            PlateSolveResult result = solver.SolveAsync(
                new PlateSolveRequest(path, Timeout: TimeSpan.FromMinutes(2))).GetAwaiter().GetResult();

            Assert.True(result.Success, $"{label}: blind solve failed -- {result.FailureReason}: {result.Message}");
            PlateSolveSolution solution = result.Solution!;

            double centreErrorArcminutes = SeparationArcminutes(
                raDegrees, decDegrees, solution.CenterRaDegrees, solution.CenterDecDegrees);
            Assert.True(centreErrorArcminutes < 5.0,
                $"{label}: solved centre is {centreErrorArcminutes:F2}' from truth");

            double recovered = PlateScale.FocalLengthMillimetres(pitchMicrons, solution.PixelScaleArcsecPerPixel);
            double errorFraction = Math.Abs(recovered - focalLengthMm) / focalLengthMm;
            Assert.True(errorFraction < 0.005,
                $"{label}: recovered focal length {recovered:F2} mm from {focalLengthMm} mm, error {errorFraction:P3}");
        }
        finally
        {
            File.Delete(path);
        }
    }

    /// <summary>
    /// The narrow end of D13's envelope, with only the bundled packs present.
    /// It must fail cleanly and say something useful, not return a wrong solve
    /// -- and this is the expected, documented outcome of O5's bundling
    /// decision, not a defect.
    /// </summary>
    [SkippableFact]
    public void NarrowField_FailsCleanlyWithoutItsIndexPack()
    {
        RequireQuadDatabase();

        TanWcsSolution truth = SkyRenderer.BuildWcs(192.86, 27.13, 947, 711, 3.8, 400.0);
        FitsImage frame = SkyRenderer.Render(Catalog, truth, 947, 711, ObservingConditions.Nominal, new Random(103));
        string path = WriteFrameWithoutWcs(frame);

        try
        {
            using var solver = new WatneyPlateSolver(QuadDatabaseDirectory);
            PlateSolveResult result = solver.SolveAsync(
                new PlateSolveRequest(path, Timeout: TimeSpan.FromMinutes(2))).GetAwaiter().GetResult();

            if (result.Success)
            {
                // If it does solve, it must at least be right -- a wrong solve
                // reported as success is the failure mode that matters.
                double error = SeparationArcminutes(192.86, 27.13, result.Solution!.CenterRaDegrees, result.Solution.CenterDecDegrees);
                Assert.True(error < 5.0, $"narrow field solved but landed {error:F2}' away, which is worse than not solving");
                return;
            }

            Assert.True(
                result.FailureReason is PlateSolveFailureReason.NoMatchFound or PlateSolveFailureReason.NoStarsDetected,
                $"expected a no-match or no-stars failure, got {result.FailureReason}: {result.Message}");
            Assert.False(string.IsNullOrWhiteSpace(result.Message));
        }
        finally
        {
            File.Delete(path);
        }
    }

    /// <summary>
    /// The full loop, and the real point of Phase 2: an injected polar
    /// misalignment must survive the whole pipeline -- forward model, inverse
    /// astrometry, render, detect, blind solve, forward astrometry, small-circle
    /// fit -- and come back within an arcminute.
    ///
    /// Every stage is the real one. Note in particular that the sequence's
    /// captures are at different times, so the sky rotates between them: the
    /// pointing directions trace a circle in the ground frame while their
    /// catalogue coordinates do not, which is exactly the relationship D15
    /// depends on and the part that would silently break if the astrometry were
    /// wrong.
    /// </summary>
    [SkippableFact]
    public void InjectedMisalignment_SurvivesTheWholePipeline()
    {
        RequireQuadDatabase();

        const double latitude = 40.0;
        const double longitude = 120.0;
        var site = new ObserverSite(latitude, longitude, 100.0);
        var injected = new MountMisalignment(AltitudeErrorArcminutes: 14.0, AzimuthErrorArcminutes: -11.0);
        var atmosphere = new AtmosphericConditions(1013.25, 10.0, 0.3, 0.55);

        // A wide field, so the bundled index pack can solve it, pointed to keep
        // the sequence inside the committed catalogue's coverage.
        const double focalLengthMm = 100.0;
        const double pitchMicrons = 3.8;
        const int width = 2737;
        const int height = 2053;

        // Chosen so the swept circle stays in the catalogue's rich field, and
        // wide enough in rotation to condition the fit well (Phase 1: sweep
        // width matters far more than capture count).
        // Site longitude and start time chosen together so that every capture
        // lands inside the committed catalogue band (RA 100-180, Dec 35-45) and
        // stays well above the horizon; the sweep is wide because Phase 1
        // measured sweep width to matter far more than capture count.
        double[] rotations = { -30.0, -18.0, -6.0, 6.0, 18.0, 30.0 };
        var start = new DateTime(2026, 9, 9, 2, 0, 0, DateTimeKind.Utc);

        HorizontalCoordinates mountAxis = MountForwardModel.MountAxis(latitude, injected);
        var trueDirections = MountForwardModel.PointingSequence(
            latitude, injected, declinationDegrees: 40.0, coneErrorArcminutes: 25.0, conePhaseDegrees: 40.0, rotations);

        var solvedDirections = new List<HorizontalCoordinates>();
        using var solver = new WatneyPlateSolver(QuadDatabaseDirectory);

        for (int i = 0; i < trueDirections.Count; i++)
        {
            DateTime timestamp = start.AddMinutes(2 * i);

            // Where the camera is looking, on the sky, at this instant.
            (double ra, double dec) = TopocentricConverter.FromAltAz(trueDirections[i], timestamp, site, atmosphere);

            TanWcsSolution truth = SkyRenderer.BuildWcs(ra, dec, width, height, pitchMicrons, focalLengthMm, rotationDegrees: 23.0);
            FitsImage frame = SkyRenderer.Render(Catalog, truth, width, height, ObservingConditions.Nominal, new Random(200 + i));

            var detected = StarDetector.Detect(frame);
            Skip.If(detected.Count < 60, $"capture {i} yielded only {detected.Count} stars, too few to solve reliably");

            string path = WriteFrameWithoutWcs(frame);
            try
            {
                PlateSolveResult result = solver.SolveAsync(
                    new PlateSolveRequest(path, Timeout: TimeSpan.FromMinutes(2))).GetAwaiter().GetResult();
                Assert.True(result.Success, $"capture {i} failed to solve -- {result.FailureReason}: {result.Message}");

                // The solve returns catalogue coordinates; the fit needs physical
                // pointing directions, so put them through the full apparent-place
                // transform, refraction included (D15).
                solvedDirections.Add(TopocentricConverter.ToAltAz(
                    result.Solution!.CenterRaDegrees, result.Solution.CenterDecDegrees, timestamp, site, atmosphere));
            }
            finally
            {
                File.Delete(path);
            }
        }

        // Plate solve centres are good to a few arcseconds; being generous here
        // makes the residual check meaningful rather than tripping on it.
        PolarAlignmentSolution recovered = PolarAlignmentSolver.Solve(solvedDirections, latitude, expectedNoiseArcseconds: 10.0);

        Assert.True(recovered.IsTrustworthy, $"the fit withheld its answer: {recovered.UntrustworthyReason}");

        double axisErrorArcminutes = SeparationArcminutes(
            mountAxis.AzimuthDegrees, mountAxis.AltitudeDegrees,
            recovered.MountAxis.AzimuthDegrees, recovered.MountAxis.AltitudeDegrees);

        Assert.True(axisErrorArcminutes < 1.0,
            $"recovered axis is {axisErrorArcminutes:F3}' from the injected one " +
            $"(altitude {recovered.AltitudeErrorArcminutes:F2}' vs {injected.AltitudeErrorArcminutes}', " +
            $"azimuth {recovered.AzimuthErrorArcminutes:F2}' vs {injected.AzimuthErrorArcminutes}')");

        // Cone error was injected at 25 arcminutes and must have made no
        // difference: it changes the circle's radius, never its axis.
        Assert.True(recovered.Fit.RadiusDegrees > 0.0);
    }

    /// <summary>
    /// Degraded frames must fail cleanly rather than return a wrong solve. This
    /// is the requirement that matters most in Phase 2, because a confident
    /// wrong solve propagates straight into the alignment result.
    /// </summary>
    [SkippableTheory]
    [InlineData("heavy cloud")]
    [InlineData("badly trailed")]
    [InlineData("badly defocused")]
    public void DegradedFrames_FailCleanlyOrSolveCorrectly(string degradation)
    {
        RequireQuadDatabase();

        const double raDegrees = 300.0;
        const double decDegrees = 35.0;
        TanWcsSolution truth = SkyRenderer.BuildWcs(raDegrees, decDegrees, 2737, 2053, 3.8, 100.0);

        ObservingConditions conditions = degradation switch
        {
            "heavy cloud" => ObservingConditions.Nominal with { CloudTransmission = 0.02, CloudGradientFraction = 0.6 },
            "badly trailed" => ObservingConditions.Nominal with { TrailLengthPixels = 25.0, TrailAngleDegrees = 40.0 },
            "badly defocused" => ObservingConditions.Nominal with { SeeingFwhmPixels = 25.0 },
            _ => throw new ArgumentOutOfRangeException(nameof(degradation)),
        };

        FitsImage frame = SkyRenderer.Render(Catalog, truth, 2737, 2053, conditions, new Random(307));
        string path = WriteFrameWithoutWcs(frame);

        try
        {
            using var solver = new WatneyPlateSolver(QuadDatabaseDirectory);
            PlateSolveResult result = solver.SolveAsync(
                new PlateSolveRequest(path, Timeout: TimeSpan.FromMinutes(2))).GetAwaiter().GetResult();

            if (result.Success)
            {
                // Solving a degraded frame is fine. Solving it *wrongly* is not:
                // that is the outcome the whole project treats as unacceptable.
                double error = SeparationArcminutes(
                    raDegrees, decDegrees, result.Solution!.CenterRaDegrees, result.Solution.CenterDecDegrees);
                Assert.True(error < 5.0,
                    $"{degradation}: reported success but landed {error:F2}' away -- a confident wrong solve");
            }
            else
            {
                Assert.False(string.IsNullOrWhiteSpace(result.Message), $"{degradation}: failed without a message");
                Assert.NotEqual(PlateSolveFailureReason.InvalidImage, result.FailureReason);
            }
        }
        finally
        {
            File.Delete(path);
        }
    }

    private static double SeparationArcminutes(double lon1, double lat1, double lon2, double lat2)
    {
        double d2r = Math.PI / 180.0;
        double phi1 = lat1 * d2r, phi2 = lat2 * d2r;
        double dPhi = phi2 - phi1;
        double dLambda = (lon2 - lon1) * d2r;

        double h = Math.Sin(dPhi / 2.0) * Math.Sin(dPhi / 2.0)
                 + Math.Cos(phi1) * Math.Cos(phi2) * Math.Sin(dLambda / 2.0) * Math.Sin(dLambda / 2.0);
        return 2.0 * Math.Asin(Math.Sqrt(Math.Clamp(h, 0.0, 1.0))) * 180.0 / Math.PI * 60.0;
    }
}
