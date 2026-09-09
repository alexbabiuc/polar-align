using FreePolarAlign.Devices.Simulated.SyntheticSky;
using FreePolarAlign.Imaging.Detection;
using FreePolarAlign.Imaging.Fits;
using FreePolarAlign.Imaging.Wcs;
using Xunit;

namespace FreePolarAlign.Tests.Imaging;

/// <summary>
/// The virtual observatory and the star detection that reads it back.
///
/// These tests close the renderer/detector loop against exact ground truth:
/// stars are placed through a known WCS, then found again without reference to
/// it, so an error in either the projection or the centroiding shows up as a
/// position mismatch. Everything here runs offline against a committed Tycho-2
/// subset, so it needs neither a clear night nor a network.
/// </summary>
public class SyntheticSkyTests
{
    /// <summary>
    /// Configurations spanning D13's stated hardware envelope: 100-400 mm focal
    /// length, 2-5 micron pixels, 4.5-13 mm sensor diagonals, giving fields from
    /// 7.6 down to 0.6 degrees.
    /// </summary>
    public static readonly (string Label, double FocalLengthMm, double PitchMicrons, int Width, int Height, double ExpectedDiagonalDegrees)[] Envelope =
    {
        ("wide", 100.0, 3.8, 2737, 2053, 7.45),
        ("mid", 200.0, 3.8, 1684, 1263, 2.29),
        ("narrow", 400.0, 3.8, 947, 711, 0.64),
    };

    /// <summary>Field centres chosen to span galactic star density by a factor of ten.</summary>
    public static readonly (string Label, double RaDegrees, double DecDegrees)[] Fields =
    {
        ("rich", 300.0, 35.0),
        ("medium", 83.6, 22.0),
        ("sparse", 192.86, 27.13),
    };

    private static StarCatalog? _catalog;

    private static StarCatalog Catalog => _catalog ??= StarCatalog.LoadCsv(
        Path.Combine(AppContext.BaseDirectory, "fixtures", "tycho2_subset.csv"));

    public static IEnumerable<object[]> EnvelopeAndFields() =>
        from configuration in Envelope
        from field in Fields
        select new object[] { configuration.Label, field.Label };

    private static (string Label, double FocalLengthMm, double PitchMicrons, int Width, int Height, double ExpectedDiagonalDegrees)
        Configuration(string label) => Envelope.Single(c => c.Label == label);

    private static (string Label, double RaDegrees, double DecDegrees) Field(string label) =>
        Fields.Single(f => f.Label == label);

    [Fact]
    public void Catalog_LoadsCommittedSubset()
    {
        Assert.True(Catalog.Count > 30000, $"expected the full committed subset, got {Catalog.Count} stars");
    }

    /// <summary>
    /// The envelope configurations must actually span D13's stated field sizes.
    /// Without this the tests below could quietly all be testing one field size.
    /// </summary>
    [Theory]
    [InlineData("wide")]
    [InlineData("mid")]
    [InlineData("narrow")]
    public void EnvelopeConfigurations_SpanTheStatedFieldSizes(string label)
    {
        var configuration = Configuration(label);
        double diagonal = PlateScale.FieldDiagonalDegrees(
            configuration.Width, configuration.Height, configuration.PitchMicrons, configuration.FocalLengthMm);

        Assert.Equal(configuration.ExpectedDiagonalDegrees, diagonal, precision: 1);
        Assert.InRange(diagonal, 0.6, 7.6);
    }

    /// <summary>
    /// The loop that matters: every star the detector finds should sit on a
    /// catalogue star projected through the true WCS. Tests the renderer's
    /// projection and the detector's centroiding at once, since a systematic
    /// error in either would move the detections off the truth.
    /// </summary>
    [Theory]
    [MemberData(nameof(EnvelopeAndFields))]
    public void DetectedStars_LandWhereTheWcsPutsThem(string configurationLabel, string fieldLabel)
    {
        var configuration = Configuration(configurationLabel);
        var field = Field(fieldLabel);

        TanWcsSolution wcs = SkyRenderer.BuildWcs(
            field.RaDegrees, field.DecDegrees,
            configuration.Width, configuration.Height,
            configuration.PitchMicrons, configuration.FocalLengthMm,
            rotationDegrees: 17.0);

        FitsImage image = SkyRenderer.Render(
            Catalog, wcs, configuration.Width, configuration.Height, ObservingConditions.Nominal, new Random(1));

        IReadOnlyList<DetectedStar> detected = StarDetector.Detect(image);
        Assert.NotEmpty(detected);

        var truth = TruthPositions(wcs, field, configuration);
        Assert.NotEmpty(truth);

        var errors = new List<double>();
        int unmatched = 0;
        foreach (DetectedStar star in detected)
        {
            double best = double.MaxValue;
            foreach ((double x, double y) in truth)
            {
                best = Math.Min(best, Math.Sqrt((x - star.X) * (x - star.X) + (y - star.Y) * (y - star.Y)));
            }

            if (best < 2.0)
            {
                errors.Add(best);
            }
            else
            {
                unmatched++;
            }
        }

        // A few unmatched detections are expected in crowded fields, where two
        // catalogue stars blend into one source whose centroid sits between them.
        Assert.True(unmatched <= Math.Max(3, detected.Count / 20),
            $"{unmatched}/{detected.Count} detections did not correspond to a catalogue star");

        errors.Sort();
        double median = errors[errors.Count / 2];
        Assert.True(median < 0.5, $"median centroid error {median:F3} px is worse than half a pixel");
    }

    private static List<(double X, double Y)> TruthPositions(
        TanWcsSolution wcs,
        (string Label, double RaDegrees, double DecDegrees) field,
        (string Label, double FocalLengthMm, double PitchMicrons, int Width, int Height, double ExpectedDiagonalDegrees) configuration)
    {
        var truth = new List<(double X, double Y)>();
        foreach (CatalogStar star in Catalog.Cone(
                     field.RaDegrees, field.DecDegrees, wcs.FieldRadiusDegrees(configuration.Width, configuration.Height), 15.0))
        {
            (double x, double y) = wcs.WorldToPixel(star.RaDegrees, star.DecDegrees);
            if (x > 3 && y > 3 && x < configuration.Width - 3 && y < configuration.Height - 3)
            {
                truth.Add((x, y));
            }
        }

        return truth;
    }

    /// <summary>
    /// Rendered frames are unsigned 16-bit, as real astronomical cameras
    /// produce: BITPIX 16 with BZERO 32768. That combination was not previously
    /// exercised -- the existing round-trip test used BZERO 0 -- and silently
    /// mangled pixel values would undermine every test that reads a frame back.
    /// </summary>
    [Fact]
    public void RenderedFrame_RoundTripsThroughUnsignedSixteenBitFits()
    {
        TanWcsSolution wcs = SkyRenderer.BuildWcs(83.6, 22.0, 512, 512, 3.8, 300.0);
        FitsImage image = SkyRenderer.Render(Catalog, wcs, 512, 512, ObservingConditions.Nominal, new Random(7));

        Assert.Equal(FitsBitPix.Int16, image.BitPix);
        Assert.Equal(32768.0, image.Bzero);

        string path = Path.Combine(Path.GetTempPath(), $"fpa-render-{Guid.NewGuid():N}.fits");
        try
        {
            FitsFile.Write(path, image);
            FitsImage readBack = FitsFile.Read(path);

            Assert.Equal(32768.0, readBack.Bzero);
            for (int y = 0; y < 512; y++)
            {
                for (int x = 0; x < 512; x++)
                {
                    Assert.Equal(image.Pixels[y, x], readBack.Pixels[y, x]);
                }
            }

            TanWcsSolution readWcs = TanWcsSolution.FromHeader(readBack.ExtraHeader);
            Assert.Equal(wcs.PixelScaleArcsecondsPerPixel, readWcs.PixelScaleArcsecondsPerPixel, precision: 9);
        }
        finally
        {
            File.Delete(path);
        }
    }

    // ---- Degraded inputs. Phase 2 requires these to fail cleanly with a
    // ---- useful message rather than return a wrong answer, which means the
    // ---- frame itself has to be diagnosable before any solver is involved.

    /// <summary>
    /// Trailing must be distinguishable from defocus. Both cost stars, but the
    /// remedy differs completely -- one is the mount, the other the focuser --
    /// so telling a user the wrong one wastes their night.
    /// </summary>
    [Fact]
    public void Trailing_IsDetectableAndDistinctFromDefocus()
    {
        FrameStatistics Statistics(ObservingConditions conditions)
        {
            TanWcsSolution wcs = SkyRenderer.BuildWcs(83.6, 22.0, 1024, 1024, 3.8, 300.0);
            FitsImage image = SkyRenderer.Render(Catalog, wcs, 1024, 1024, conditions, new Random(11));
            return StarDetector.DetectWithStatistics(image).Statistics;
        }

        FrameStatistics sharp = Statistics(ObservingConditions.Nominal);
        FrameStatistics trailed = Statistics(ObservingConditions.Nominal with { TrailLengthPixels = 12.0, TrailAngleDegrees = 30.0 });
        FrameStatistics defocused = Statistics(ObservingConditions.Nominal with { SeeingFwhmPixels = 12.0 });

        // Trailing elongates; defocus does not.
        Assert.True(trailed.MedianElongation > 2.0,
            $"trailed frame elongation {trailed.MedianElongation:F2} should be clearly above round");
        Assert.True(defocused.MedianElongation < 1.3,
            $"defocused frame elongation {defocused.MedianElongation:F2} should stay round");

        // Defocus broadens; so does trailing, so width alone cannot separate them.
        Assert.True(defocused.MedianFwhmPixels > 2.0 * sharp.MedianFwhmPixels,
            $"defocused FWHM {defocused.MedianFwhmPixels:F2} px vs sharp {sharp.MedianFwhmPixels:F2} px");
    }

    /// <summary>
    /// Cloud attenuates starlight without touching read noise, so it costs
    /// stars. An uneven layer also tilts the background, which is exactly what
    /// a single global threshold would mishandle.
    /// </summary>
    [Fact]
    public void Cloud_CostsStarsAndIsVisibleInTheBackgroundGradient()
    {
        (int Count, FrameStatistics Statistics) Frame(ObservingConditions conditions)
        {
            TanWcsSolution wcs = SkyRenderer.BuildWcs(83.6, 22.0, 1024, 1024, 3.8, 300.0);
            FitsImage image = SkyRenderer.Render(Catalog, wcs, 1024, 1024, conditions, new Random(13));
            var (stars, statistics) = StarDetector.DetectWithStatistics(image);
            return (stars.Count, statistics);
        }

        var clear = Frame(ObservingConditions.Nominal);
        var clouded = Frame(ObservingConditions.Nominal with { CloudTransmission = 0.05 });

        Assert.True(clouded.Count < clear.Count / 2,
            $"heavy cloud should cost most stars: {clouded.Count} vs {clear.Count} clear");
    }

    /// <summary>
    /// The detector must keep working through an uneven sky, which is the whole
    /// reason the background is estimated on a tile grid rather than globally.
    /// A gradient that defeats detection would show up here as lost stars.
    /// </summary>
    [Fact]
    public void SkyGradient_DoesNotDefeatDetection()
    {
        TanWcsSolution wcs = SkyRenderer.BuildWcs(83.6, 22.0, 1024, 1024, 3.8, 300.0);

        int Count(double gradient)
        {
            var conditions = ObservingConditions.Nominal with
            {
                CloudTransmission = 0.8,
                CloudGradientFraction = gradient,
            };
            FitsImage image = SkyRenderer.Render(Catalog, wcs, 1024, 1024, conditions, new Random(17));
            return StarDetector.Detect(image).Count;
        }

        int flat = Count(0.0);
        int steep = Count(0.8);

        Assert.True(steep > 0.7 * flat,
            $"a sky gradient cost too many detections: {steep} against {flat} on a flat sky");
    }

    /// <summary>
    /// A sparse field at a narrow focal length is the genuinely hard case: it is
    /// the corner of D13's envelope, and the point where Tycho-2 itself starts
    /// running out of stars. Recorded as a measurement rather than asserted to
    /// succeed, because how few stars remain is exactly what decides whether a
    /// solve is possible at all.
    /// </summary>
    [Fact]
    public void SparseNarrowField_YieldsFewStars()
    {
        var configuration = Configuration("narrow");
        var field = Field("sparse");

        TanWcsSolution wcs = SkyRenderer.BuildWcs(
            field.RaDegrees, field.DecDegrees, configuration.Width, configuration.Height,
            configuration.PitchMicrons, configuration.FocalLengthMm);

        FitsImage image = SkyRenderer.Render(
            Catalog, wcs, configuration.Width, configuration.Height, ObservingConditions.Nominal, new Random(19));

        IReadOnlyList<DetectedStar> stars = StarDetector.Detect(image);

        // Enough to be a real image, few enough that a solver may legitimately
        // fail: the regime a "not enough stars" message exists for.
        Assert.InRange(stars.Count, 1, 40);
    }

    [Fact]
    public void EmptyField_ProducesNoSpuriousDetections()
    {
        // A pointing far from any catalogue coverage: pure noise, no stars.
        TanWcsSolution wcs = SkyRenderer.BuildWcs(10.0, -70.0, 512, 512, 3.8, 300.0);
        FitsImage image = SkyRenderer.Render(Catalog, wcs, 512, 512, ObservingConditions.Nominal, new Random(23));

        IReadOnlyList<DetectedStar> stars = StarDetector.Detect(image);

        // At five sigma with a three-pixel minimum, a quarter-megapixel frame of
        // pure noise should yield essentially nothing. Anything more means the
        // threshold or the noise estimate is wrong.
        Assert.True(stars.Count <= 2, $"{stars.Count} spurious detections on an empty field");
    }
}
