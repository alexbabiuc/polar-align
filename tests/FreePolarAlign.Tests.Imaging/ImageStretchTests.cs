using FreePolarAlign.Imaging.Display;
using FreePolarAlign.Imaging.Fits;
using Xunit;

namespace FreePolarAlign.Tests.Imaging;

/// <summary>
/// The display stretch.
///
/// It has one job: make a frame that is genuinely almost black show something a
/// person can judge. The test that matters most is therefore not about pixel
/// values at all — it is that a realistic exposure, which a linear rendering
/// turns into a black rectangle, comes out with a visible background and stars
/// that still stand out from it.
///
/// The transform is also required to be order-preserving. A display transform
/// that reordered two pixels would show a fainter star as brighter than a
/// brighter one, which is worse than showing nothing.
/// </summary>
public class ImageStretchTests
{
    /// <summary>
    /// A frame shaped like a real one: a bias pedestal, faint sky above it, a
    /// little noise, and a few stars orders of magnitude brighter. The stars
    /// occupy a tiny fraction of the pixels and almost all of the range, which
    /// is exactly what defeats a linear rendering.
    /// </summary>
    private static FitsImage RealisticFrame(
        int width = 200, int height = 150, double background = 480.0, double noise = 12.0, int seed = 7)
    {
        var random = new Random(seed);
        var pixels = new double[height, width];

        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                // Box-Muller, so the noise is Gaussian rather than uniform --
                // the median-absolute-deviation scaling assumes Gaussian.
                double u1 = 1.0 - random.NextDouble();
                double u2 = random.NextDouble();
                double gaussian = Math.Sqrt(-2.0 * Math.Log(u1)) * Math.Cos(2.0 * Math.PI * u2);
                pixels[y, x] = Math.Max(0.0, background + (noise * gaussian));
            }
        }

        // A handful of bright stars, each a few pixels across.
        (int X, int Y, double Peak)[] stars =
        {
            (30, 40, 40000.0), (120, 90, 22000.0), (170, 20, 9000.0), (60, 120, 5000.0),
        };

        foreach ((int starX, int starY, double peak) in stars)
        {
            for (int dy = -2; dy <= 2; dy++)
            {
                for (int dx = -2; dx <= 2; dx++)
                {
                    int x = starX + dx;
                    int y = starY + dy;
                    if (x < 0 || y < 0 || x >= width || y >= height)
                    {
                        continue;
                    }

                    double falloff = Math.Exp(-((dx * dx) + (dy * dy)) / 2.0);
                    pixels[y, x] = Math.Min(65535.0, pixels[y, x] + (peak * falloff));
                }
            }
        }

        return new FitsImage(width, height, FitsBitPix.Int16, 32768.0, 1.0, pixels);
    }

    private static FitsImage Flat(double value, int width = 40, int height = 30)
    {
        var pixels = new double[height, width];
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                pixels[y, x] = value;
            }
        }

        return new FitsImage(width, height, FitsBitPix.Int16, 32768.0, 1.0, pixels);
    }

    private static double MeanOf(byte[] gray) => gray.Average(b => (double)b);

    private static int CountAtValue(FitsImage image, double value)
    {
        int count = 0;
        for (int y = 0; y < image.Height; y++)
        {
            for (int x = 0; x < image.Width; x++)
            {
                if (image.Pixels[y, x] == value)
                {
                    count++;
                }
            }
        }

        return count;
    }

    // ---- Full range, nothing clipped ----

    /// <summary>
    /// The frame's own darkest and brightest pixels are the black and white
    /// points, so the preview uses the whole output range.
    /// </summary>
    [Theory]
    [InlineData(0.05)]
    [InlineData(0.25)]
    [InlineData(0.7)]
    public void TheDarkestPixelIsBlackAndTheBrightestIsWhite(double target)
    {
        ImagePreview preview = ImageStretch.Create(RealisticFrame(), target);

        Assert.Equal(0, preview.Gray8.Min());
        Assert.Equal(255, preview.Gray8.Max());
    }

    /// <summary>
    /// The guarantee this stretch is built around: a pixel renders as black only
    /// if it *is* the darkest pixel in the frame.
    ///
    /// An earlier version clipped everything below median - 2.8 sigma, which on
    /// this frame is several hundred pixels -- the entire bottom tail of the
    /// noise, flattened to a single value. Checked as an exact count rather than
    /// a proportion, because "hardly anything is clipped" is the claim that
    /// version would also have passed.
    /// </summary>
    [Fact]
    public void OnlyTheDarkestPixelsRenderBlack()
    {
        FitsImage frame = RealisticFrame();
        ImagePreview preview = ImageStretch.Create(frame, ImageStretch.DefaultTargetBackground);

        Assert.Equal(1, preview.Decimation);

        int darkestInSource = CountAtValue(frame, preview.Statistics.MinimumAdu);
        int blackInPreview = preview.Gray8.Count(b => b == 0);

        Assert.Equal(darkestInSource, blackInPreview);
    }

    /// <summary>
    /// The same at the top: only the brightest pixel saturates. A preview that
    /// blew the cores of every star to a single flat white would hide
    /// saturation, which is one of the things a person checks a frame for.
    /// </summary>
    [Fact]
    public void OnlyTheBrightestPixelsRenderWhite()
    {
        FitsImage frame = RealisticFrame();
        ImagePreview preview = ImageStretch.Create(frame, ImageStretch.DefaultTargetBackground);

        int brightestInSource = CountAtValue(frame, preview.Statistics.MaximumAdu);
        int whiteInPreview = preview.Gray8.Count(b => b == 255);

        Assert.Equal(brightestInSource, whiteInPreview);
    }

    /// <summary>
    /// The bottom of the noise survives as structure rather than as a flat
    /// floor. This is what the old shadow clipping destroyed, and it is the
    /// reason to care: an uneven background -- dew, twilight, a light leak -- is
    /// read off the dark end of the histogram, and a display that crushes it
    /// answers "is the background even?" with a confident yes whatever the truth.
    /// </summary>
    [Fact]
    public void TheDarkEndKeepsItsStructure()
    {
        FitsImage frame = RealisticFrame();
        ImagePreview preview = ImageStretch.Create(frame, ImageStretch.DefaultTargetBackground);

        // Distinct levels strictly below the background, where the old version
        // had exactly one: black.
        double normalisedMedian =
            (preview.Statistics.MedianAdu - preview.Statistics.BlackPointAdu)
            / (preview.Statistics.MaximumAdu - preview.Statistics.BlackPointAdu);
        byte backgroundLevel = (byte)Math.Round(
            ImageStretch.ApplyMidtone(preview.Statistics.Midtone, normalisedMedian) * 255.0);

        int levelsBelowBackground = preview.Gray8.Where(b => b < backgroundLevel).Distinct().Count();

        Assert.True(
            levelsBelowBackground > 20,
            $"only {levelsBelowBackground} distinct grey levels below the background of {backgroundLevel}");
    }

    /// <summary>
    /// The brightness control is a histogram stretch, not a black point: moving
    /// it changes the curve between the endpoints and leaves the endpoints where
    /// the frame put them.
    /// </summary>
    [Fact]
    public void TheBrightnessControlLeavesTheBlackAndWhitePointsAlone()
    {
        FitsImage frame = RealisticFrame();

        StretchStatistics dark = ImageStretch.Create(frame, 0.05).Statistics;
        StretchStatistics bright = ImageStretch.Create(frame, 0.8).Statistics;

        Assert.Equal(dark.BlackPointAdu, bright.BlackPointAdu);
        Assert.Equal(dark.MaximumAdu, bright.MaximumAdu);
        Assert.Equal(dark.MinimumAdu, dark.BlackPointAdu);

        // And it does change something, or it would not be a control.
        Assert.NotEqual(dark.Midtone, bright.Midtone);
    }

    // ---- The point of the whole thing ----

    /// <summary>
    /// The reason an automatic stretch is the default. A faithful rendering of a
    /// real exposure is black, because the sky sits a few hundred ADU above bias
    /// while the stars reach forty thousand -- so the background lands within a
    /// couple of levels of zero out of 255.
    /// </summary>
    [Fact]
    public void ALinearRenderingOfARealFrame_IsEffectivelyBlack()
    {
        ImagePreview linear = ImageStretch.CreateLinear(RealisticFrame());

        Assert.True(
            MeanOf(linear.Gray8) < 6.0,
            $"a linear rendering averaged {MeanOf(linear.Gray8):F1}/255, which would not have needed stretching");
    }

    /// <summary>
    /// And the stretched one puts the sky background near the requested level,
    /// which is the whole contract.
    /// </summary>
    [Theory]
    [InlineData(0.10)]
    [InlineData(0.25)]
    [InlineData(0.50)]
    public void TheStretchedBackground_LandsWhereItWasAsked(double target)
    {
        FitsImage frame = RealisticFrame();
        ImagePreview preview = ImageStretch.Create(frame, target);

        // The median of the *preview* is biased upward by the max-pooling, so
        // the check is against the transform itself at the measured background.
        StretchStatistics statistics = preview.Statistics;
        double normalisedMedian =
            (statistics.MedianAdu - statistics.BlackPointAdu)
            / (statistics.MaximumAdu - statistics.BlackPointAdu);

        Assert.Equal(target, ImageStretch.ApplyMidtone(statistics.Midtone, normalisedMedian), precision: 3);
    }

    /// <summary>
    /// Dragging the slider right makes the image lighter, monotonically. This is
    /// the user-visible promise of the control, and it has to hold across the
    /// whole range rather than only near the default.
    /// </summary>
    [Fact]
    public void RaisingTheTarget_MakesThePictureLighter()
    {
        FitsImage frame = RealisticFrame();
        double[] targets = { 0.05, 0.1, 0.2, 0.3, 0.45, 0.6, 0.8 };

        double[] brightness = targets
            .Select(t => MeanOf(ImageStretch.Create(frame, t).Gray8))
            .ToArray();

        for (int i = 1; i < brightness.Length; i++)
        {
            Assert.True(
                brightness[i] > brightness[i - 1],
                $"target {targets[i]} was darker ({brightness[i]:F1}) than {targets[i - 1]} ({brightness[i - 1]:F1})");
        }
    }

    /// <summary>
    /// Stars must still be distinguishable from the sky after stretching. A
    /// stretch aggressive enough to wash them out would defeat the purpose of
    /// looking at the frame -- which is usually to answer "are there any stars in
    /// this at all".
    /// </summary>
    [Fact]
    public void AfterStretching_StarsAreStillBrighterThanTheSky()
    {
        ImagePreview preview = ImageStretch.Create(RealisticFrame(), ImageStretch.DefaultTargetBackground);

        double background = MeanOf(preview.Gray8);
        byte brightest = preview.Gray8.Max();

        Assert.True(brightest > 240, $"the brightest pixel came out at {brightest}/255");
        Assert.True(
            brightest - background > 100.0,
            $"stars stood only {brightest - background:F0} levels above a background of {background:F0}");
    }

    // ---- Order preservation ----

    /// <summary>
    /// The transfer function is strictly increasing, so no two pixels swap
    /// order. A display transform that reordered them would show a fainter star
    /// as the brighter one.
    /// </summary>
    [Theory]
    [InlineData(0.02)]
    [InlineData(0.2)]
    [InlineData(0.5)]
    [InlineData(0.8)]
    [InlineData(0.98)]
    public void TheTransferFunctionIsMonotonic(double midtone)
    {
        double previous = -1.0;

        for (int i = 0; i <= 1000; i++)
        {
            double x = i / 1000.0;
            double y = ImageStretch.ApplyMidtone(midtone, x);

            Assert.InRange(y, 0.0, 1.0);
            Assert.True(y >= previous, $"midtone {midtone} was not monotonic at x={x}");
            previous = y;
        }
    }

    /// <summary>
    /// The function's three fixed points, which are what make it usable as a
    /// display transform: black stays black, white stays white, and the midtone
    /// lands exactly halfway.
    /// </summary>
    [Theory]
    [InlineData(0.05)]
    [InlineData(0.25)]
    [InlineData(0.5)]
    [InlineData(0.75)]
    public void TheEndpointsAreFixedAndTheMidtoneLandsAtAHalf(double midtone)
    {
        Assert.Equal(0.0, ImageStretch.ApplyMidtone(midtone, 0.0), precision: 12);
        Assert.Equal(1.0, ImageStretch.ApplyMidtone(midtone, 1.0), precision: 12);
        Assert.Equal(0.5, ImageStretch.ApplyMidtone(midtone, midtone), precision: 9);
    }

    /// <summary>
    /// A midtone of one half is the identity, so "no stretch" is available
    /// through the same code path rather than as a special case.
    /// </summary>
    [Theory]
    [InlineData(0.0)]
    [InlineData(0.1)]
    [InlineData(0.5)]
    [InlineData(0.9)]
    [InlineData(1.0)]
    public void AMidtoneOfAHalfIsTheIdentity(double x) =>
        Assert.Equal(x, ImageStretch.ApplyMidtone(0.5, x), precision: 9);

    /// <summary>
    /// The midtone is solved for, not searched, so it has to invert the
    /// transform exactly -- feeding it back in must return the target.
    /// </summary>
    [Theory]
    [InlineData(0.001, 0.25)]
    [InlineData(0.01, 0.25)]
    [InlineData(0.1, 0.5)]
    [InlineData(0.25, 0.25)]
    [InlineData(0.6, 0.1)]
    public void TheMidtoneSolvesTheTransformExactly(double median, double target)
    {
        double midtone = ImageStretch.Midtone(median, target);

        Assert.Equal(target, ImageStretch.ApplyMidtone(midtone, median), precision: 9);
    }

    /// <summary>
    /// A median already at the target needs no stretch at all, which the closed
    /// form should produce on its own rather than by a special case.
    /// </summary>
    [Fact]
    public void AMedianAlreadyAtTheTarget_NeedsNoStretch() =>
        Assert.Equal(0.5, ImageStretch.Midtone(0.25, 0.25), precision: 9);

    // ---- Downsampling ----

    /// <summary>
    /// A big frame is decimated for display, and the preview reports both the
    /// factor and the source size so the caption can be honest about showing a
    /// reduced image.
    /// </summary>
    [Fact]
    public void ALargeFrameIsDecimated_AndSaysByHowMuch()
    {
        FitsImage frame = RealisticFrame(width: 2000, height: 1500);
        ImagePreview preview = ImageStretch.Create(frame, maximumWidth: 500, maximumHeight: 500);

        Assert.True(preview.Decimation >= 3, $"decimation was only {preview.Decimation}");
        Assert.InRange(preview.Width, 1, 500);
        Assert.InRange(preview.Height, 1, 500);
        Assert.Equal(2000, preview.SourceWidth);
        Assert.Equal(1500, preview.SourceHeight);
        Assert.Equal(preview.Width * preview.Height, preview.Gray8.Length);
    }

    /// <summary>
    /// Decimation takes the brightest pixel in each block, not the average.
    ///
    /// This is the decision worth testing. A star two pixels across, decimated
    /// by four, survives max-pooling and would be diluted eightfold by
    /// averaging -- or missed altogether by subsampling. Since the commonest
    /// reason to look at the preview is "are there stars in this", losing them to
    /// the resize would defeat it.
    /// </summary>
    [Fact]
    public void DecimationKeepsTheBrightestPixel_NotTheAverage()
    {
        // A flat frame with one hot pixel, decimated hard enough that averaging
        // would bury it.
        var pixels = new double[64, 64];
        for (int y = 0; y < 64; y++)
        {
            for (int x = 0; x < 64; x++)
            {
                pixels[y, x] = 100.0;
            }
        }

        pixels[32, 32] = 60000.0;

        var frame = new FitsImage(64, 64, FitsBitPix.Int16, 32768.0, 1.0, pixels);
        ImagePreview preview = ImageStretch.Create(frame, maximumWidth: 8, maximumHeight: 8);

        Assert.Equal(8, preview.Decimation);
        Assert.Equal(255, preview.Gray8.Max());
    }

    /// <summary>
    /// Row zero of the preview is the top of the display, which is the opposite
    /// of the FITS convention. A preview shown upside down relative to the solved
    /// orientation would be actively misleading once the correction reticle
    /// (D12) is drawn over it.
    /// </summary>
    [Fact]
    public void ThePreviewIsFlipped_SoRowZeroIsTheTopOfTheDisplay()
    {
        // Bright along the *first* stored row, which FITS puts at the bottom.
        var pixels = new double[10, 10];
        for (int x = 0; x < 10; x++)
        {
            pixels[0, x] = 60000.0;
        }

        var frame = new FitsImage(10, 10, FitsBitPix.Int16, 32768.0, 1.0, pixels);
        ImagePreview preview = ImageStretch.CreateLinear(frame);

        byte topLeft = preview.Gray8[0];
        byte bottomLeft = preview.Gray8[(preview.Height - 1) * preview.Width];

        Assert.True(bottomLeft > topLeft, "the first stored row should appear at the bottom of the display");
    }

    // ---- Degenerate frames ----

    /// <summary>
    /// A uniform frame -- lens cap on, or a driver returning zeroes -- renders
    /// mid-grey rather than black. Black would be indistinguishable from a
    /// preview that had failed to load, and the user would not know which
    /// problem they had.
    /// </summary>
    [Theory]
    [InlineData(0.0)]
    [InlineData(1000.0)]
    [InlineData(65535.0)]
    public void AUniformFrameRendersGrey_NotBlack(double value)
    {
        ImagePreview preview = ImageStretch.Create(Flat(value));

        Assert.All(preview.Gray8, b => Assert.Equal(128, b));
    }

    /// <summary>
    /// Noise-free but non-uniform data has a median absolute deviation of zero,
    /// which would make the black point calculation divide by nothing. It has to
    /// produce a usable picture rather than a crash or a blank.
    /// </summary>
    [Fact]
    public void AFrameWithNoNoise_StillProducesAPicture()
    {
        var pixels = new double[20, 20];
        for (int y = 0; y < 20; y++)
        {
            for (int x = 0; x < 20; x++)
            {
                pixels[y, x] = 500.0;
            }
        }

        pixels[10, 10] = 4000.0;

        ImagePreview preview = ImageStretch.Create(new FitsImage(20, 20, FitsBitPix.Int16, 32768.0, 1.0, pixels));

        Assert.Equal(400, preview.Gray8.Length);
        Assert.True(preview.Gray8.Max() > preview.Gray8.Min(), "the bright pixel should still be distinguishable");
    }

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(0.0)]
    [InlineData(-1.0)]
    [InlineData(5.0)]
    public void AnAbsurdTargetIsClamped_RatherThanCrashing(double target)
    {
        ImagePreview preview = ImageStretch.Create(RealisticFrame(), target);

        Assert.Equal(preview.Width * preview.Height, preview.Gray8.Length);
        Assert.All(preview.Gray8, b => Assert.InRange(b, 0, 255));
    }

    // ---- Bit depth ----

    /// <summary>
    /// An eight-bit frame stretches as readily as a sixteen-bit one, and it is
    /// the case the full-range normalisation has to be careful about.
    ///
    /// Eight-bit readout quantises the noise away entirely -- a 480 ADU sky with
    /// 12 ADU of noise lands on a single level -- so the background *is* the
    /// darkest value in the frame. Mapping the darkest value to 0, which is what
    /// every other frame wants, would pin the sky to black with no way to lift
    /// it: the transfer function fixes 0, so the brightness control would move
    /// nothing. Create places the black point one output level lower in exactly
    /// this case, which is what this test guards.
    /// </summary>
    [Fact]
    public void AnEightBitFrameStretchesToo()
    {
        FitsImage sixteenBit = RealisticFrame();

        var quantised = new double[sixteenBit.Height, sixteenBit.Width];
        for (int y = 0; y < sixteenBit.Height; y++)
        {
            for (int x = 0; x < sixteenBit.Width; x++)
            {
                quantised[y, x] = Math.Round(sixteenBit.Pixels[y, x] / (65535.0 / 255.0));
            }
        }

        var eightBit = new FitsImage(
            sixteenBit.Width, sixteenBit.Height, FitsBitPix.Byte, 0.0, 1.0, quantised);

        ImagePreview preview = ImageStretch.Create(eightBit, ImageStretch.DefaultTargetBackground);

        Assert.True(MeanOf(preview.Gray8) > 10.0, "an eight-bit frame came out black");
        Assert.True(preview.Gray8.Max() > 240, "the stars vanished from an eight-bit frame");
    }

    // ---- Output format ----

    [Fact]
    public void BgraExpansionIsOpaqueGrey()
    {
        ImagePreview preview = ImageStretch.Create(RealisticFrame(width: 8, height: 6));
        byte[] bgra = ImageStretch.ToBgra(preview);

        Assert.Equal(preview.Gray8.Length * 4, bgra.Length);

        for (int i = 0; i < preview.Gray8.Length; i++)
        {
            Assert.Equal(preview.Gray8[i], bgra[i * 4]);
            Assert.Equal(preview.Gray8[i], bgra[(i * 4) + 1]);
            Assert.Equal(preview.Gray8[i], bgra[(i * 4) + 2]);
            Assert.Equal(255, bgra[(i * 4) + 3]);
        }
    }
}
