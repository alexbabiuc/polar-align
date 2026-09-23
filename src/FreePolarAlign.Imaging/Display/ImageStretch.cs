using FreePolarAlign.Imaging.Fits;

namespace FreePolarAlign.Imaging.Display;

/// <summary>
/// The display curve itself: everything needed to turn a normalised pixel into a
/// normalised brightness, and nothing else.
///
/// It is a public type rather than a private lambda because the tests, and any
/// future caption or histogram overlay, need to ask what the picture on screen
/// actually did to a given value. Answering that by re-deriving it from a
/// midtone alone stopped being possible once the curve gained a second segment.
/// </summary>
/// <param name="HighlightAnchor">
/// Where the core ends and the highlight segment begins, normalised between the
/// black point and the brightest pixel. Above this the frame holds almost
/// nothing, so the range above it is compressed rather than spent.
/// </param>
/// <param name="CoreCeiling">The output brightness <paramref name="HighlightAnchor"/> maps to.</param>
/// <param name="Midtone">
/// The midtone balance of the core segment. Small values brighten hard; 0.5
/// leaves the core linear.
/// </param>
public sealed record StretchCurve(double HighlightAnchor, double CoreCeiling, double Midtone)
{
    /// <summary>The identity: no stretch, no highlight compression.</summary>
    public static StretchCurve Linear { get; } = new(1.0, 1.0, 0.5);

    /// <param name="normalised">
    /// A pixel expressed as a fraction of the way from the black point to the
    /// brightest pixel in the frame.
    /// </param>
    /// <returns>Brightness from 0 to 1. Strictly increasing, with 0 and 1 fixed.</returns>
    public double Apply(double normalised)
    {
        double x = Math.Clamp(normalised, 0.0, 1.0);

        if (x > HighlightAnchor)
        {
            // Linear to the white point. A curve here would buy nothing: the
            // segment exists to be small, and what is inside it is star cores.
            return Math.Clamp(
                CoreCeiling + ((1.0 - CoreCeiling) * (x - HighlightAnchor) / (1.0 - HighlightAnchor)),
                0.0,
                1.0);
        }

        return CoreCeiling * ImageStretch.ApplyMidtone(Midtone, x / HighlightAnchor);
    }
}

/// <param name="MedianAdu">Background level, in the image's own units. The single most useful number for "is this frame any good".</param>
/// <param name="MadAdu">
/// Median absolute deviation of the pixels. A robust noise estimate: unlike a
/// standard deviation it is barely moved by the stars, which are the outliers
/// here rather than the signal being characterised.
/// </param>
/// <param name="BlackPointAdu">
/// The value rendered as 0. Normally the darkest pixel in the frame, so the
/// output range is used fully and nothing below it is crushed -- there is
/// nothing below it. The one exception is a frame whose background *is* its
/// darkest value, described on <see cref="ImageStretch.Create"/>.
/// </param>
/// <param name="MaximumAdu">The white point: the brightest pixel, rendered as 255.</param>
/// <param name="Midtone">
/// The midtone balance of the curve's core segment. Kept as its own field
/// because it is the number the brightness control moves, but it no longer
/// describes the whole transform on its own -- use <paramref name="Curve"/> for
/// that.
/// </param>
/// <param name="Curve">The transform actually applied, black point to white point.</param>
public sealed record StretchStatistics(
    double MinimumAdu,
    double MaximumAdu,
    double MedianAdu,
    double MadAdu,
    double BlackPointAdu,
    double Midtone,
    StretchCurve Curve);

/// <param name="Decimation">
/// How many source pixels went into each preview pixel, per axis. 1 means the
/// preview is full resolution.
/// </param>
/// <param name="Gray8">
/// One byte per pixel, row 0 at the *top* of the display. FITS stores its first
/// row at the bottom, so this is flipped relative to the file -- which is what
/// every other astronomy program shows, and getting it wrong would put the
/// preview upside down relative to the solved orientation.
/// </param>
public sealed record ImagePreview(
    int Width,
    int Height,
    int SourceWidth,
    int SourceHeight,
    int Decimation,
    byte[] Gray8,
    StretchStatistics Statistics);

/// <summary>
/// Turns a captured frame into something a person can actually see.
///
/// A linear map from the darkest pixel to the brightest shows a black
/// rectangle. An astronomical exposure is almost entirely sky background sitting
/// a little above the bias, with a handful of stars orders of magnitude
/// brighter; the interesting structure occupies a tiny fraction of the range,
/// down at the bottom. So the default here is an automatic stretch rather than a
/// faithful one, and it is described as a *display* transform because that is
/// all it is -- nothing measured is computed from these bytes.
///
/// The frame's own darkest and brightest pixels are the black and white points.
/// The transform between them is strictly increasing, so no pixel is clipped at
/// either end and no two pixels swap order.
///
/// Between them the curve has two segments, and the second one is the difference
/// between a stretch and a brightness knob. A single midtone transfer function
/// spread over the whole range spends output on the part of the histogram where
/// there are no pixels, and on a frame whose background sits well up the range
/// it spends so much that the picture comes out *flatter* than no stretch at
/// all. Measured on a real over-exposed frame from a 105 mm lens and an
/// ASI290MM -- background at 40% of full range, noise 8.7% of it -- the middle
/// half of the pixels landed on grey levels 53 to 76 with the stretch and 53 to
/// 159 without it. Dragging the brightness control then did the only thing left
/// to do: slide the whole picture up and down, which is exactly what it looked
/// like from the outside.
///
/// So the range above <see cref="StretchCurve.HighlightAnchor"/> -- a point just
/// above the noise and above all but the brightest half-percent of pixels, which
/// on that frame was the top quarter of the range holding a thousandth of the
/// pixels -- is compressed instead of spent, and the freed output goes to the
/// core where the pixels actually are. The same frame then gives 50 to 83 at the
/// default setting, and going darker keeps a picture rather than fading to
/// black. Nothing is clipped: the compression is a gentler slope, never a
/// clamp, and the brightest pixel still renders as 255.
///
/// An earlier version also clipped everything below median - 2.8 sigma to black.
/// That is gone and stays gone -- it threw away the bottom of the noise
/// distribution, a quarter of a percent of pixels on a Gaussian background and
/// visibly more wherever the sky was not flat, and one of the things a person
/// looks at this preview to judge is whether the background is even.
/// </summary>
public static class ImageStretch
{
    /// <summary>
    /// Where the sky background is placed in the output range. A quarter of full
    /// brightness is dark enough that stars still stand out and light enough
    /// that faint ones are visible -- the same target the widely used
    /// implementations of this transform settle on.
    /// </summary>
    public const double DefaultTargetBackground = 0.25;

    /// <summary>
    /// The darkest and lightest the brightness control is allowed to go.
    ///
    /// Narrower than the range the transform will accept, because outside it the
    /// control stops being a stretch. There are only so many output levels below
    /// the background, and at a background of 2% of full brightness the middle
    /// half of the pixels shares four of them -- the picture goes black except
    /// for the stars, which is what a black point does, not a stretch. The same
    /// happens inverted at the top. These bounds are where the measured spread
    /// of the middle half of the pixels stops shrinking faster than the
    /// brightness changes.
    /// </summary>
    public const double MinimumTargetBackground = 0.08;

    public const double MaximumTargetBackground = 0.75;

    /// <summary>Distinct levels an eight-bit output has, which is what the preview renders to.</summary>
    private const int OutputLevels = 256;

    /// <summary>
    /// Statistics are taken from every fourth pixel on each axis. A sixteenth of
    /// a five megapixel frame is still 350,000 samples, which pins a median far
    /// tighter than a display decision needs, and it keeps the whole preview
    /// inside a few tens of milliseconds.
    /// </summary>
    private const int StatisticsStride = 4;

    /// <summary>Scales a median absolute deviation to a Gaussian standard deviation.</summary>
    private const double MadToSigma = 1.4826;

    /// <summary>
    /// How far above the background the highlight anchor is held, in noise
    /// sigmas, whatever the histogram says. The core segment has to contain the
    /// whole noise distribution or the thing the stretch exists to show ends up
    /// in the compressed part.
    /// </summary>
    private const double HighlightAnchorSigmas = 4.0;

    /// <summary>
    /// Fraction of pixels left above the highlight anchor. Half a percent is
    /// comfortably more than the star pixels of a wide-field frame, so the
    /// anchor lands above the sky and below the star cores.
    /// </summary>
    private const double HighlightAnchorQuantile = 0.995;

    /// <summary>
    /// The least the highlight segment's slope may fall to, relative to a linear
    /// rendering. Compressing the empty top of the histogram is the point, but
    /// compressing it without limit would flatten every star to the same white
    /// on a frame whose stars genuinely span that range -- which is most
    /// correctly exposed frames. A third of linear is enough to free the output
    /// that an over-exposed frame wastes and mild enough to leave star cores
    /// distinguishable.
    /// </summary>
    private const double MinimumHighlightSlope = 0.35;

    /// <summary>Output the highlight segment gets even when it covers almost no range, so the white point is never a cliff.</summary>
    private const double MinimumHighlightOutput = 0.08;

    /// <summary>
    /// Output kept above the requested background. Without it a bright setting
    /// could ask for a background above the core's own ceiling, which no midtone
    /// can deliver and which piles the whole frame onto one grey level.
    /// </summary>
    private const double TargetHeadroom = 0.08;

    /// <summary>
    /// Builds a preview using the frame's full range -- darkest pixel to black,
    /// brightest to white -- with the background placed at
    /// <paramref name="targetBackground"/>.
    ///
    /// Nothing is clipped at either end. Every pixel strictly between the
    /// darkest and the brightest lands strictly between 0 and 255, because the
    /// transform applied is strictly increasing and fixes both endpoints. A
    /// pixel renders as 0 exactly when it is the darkest in the frame, and 255
    /// exactly when it is the brightest.
    ///
    /// The one exception, and it has to be one: a frame whose background *is*
    /// its darkest value. An eight-bit readout does this -- it quantises the
    /// noise away entirely, so a 600 ADU sky with 9 ADU of noise becomes a
    /// single level with nothing below it. Mapping that level to 0 would pin the
    /// sky to black with no way to lift it, since every monotonic transform that
    /// fixes 0 leaves it there, and the brightness control would do nothing at
    /// all. So in that case alone the black point is placed one output level
    /// below the darkest pixel, which is the smallest displacement that gives
    /// the sky somewhere to be lifted from, and still clips nothing.
    /// </summary>
    /// <param name="targetBackground">
    /// Higher is lighter. Clamped to a range that stays meaningful: at zero the
    /// transform is undefined, and above about 0.9 everything but the darkest
    /// pixels saturates to white.
    /// </param>
    public static ImagePreview Create(
        FitsImage image,
        double targetBackground = DefaultTargetBackground,
        int maximumWidth = 1024,
        int maximumHeight = 1024)
    {
        ArgumentNullException.ThrowIfNull(image);

        double target = Math.Clamp(double.IsFinite(targetBackground) ? targetBackground : DefaultTargetBackground, 0.01, 0.9);

        (double minimum, double maximum) = Range(image);

        if (maximum - minimum <= 0.0)
        {
            // A uniform frame -- lens cap on, or a driver returning zeroes.
            // Mid-grey rather than black, so the user can see the preview is
            // working and the frame is not.
            return Uniform(image, minimum, maximum, maximumWidth, maximumHeight);
        }

        double low = minimum;
        double span = maximum - low;
        double[] samples = Sample(image, low, span);
        double medianNormalised = MedianOf(samples);

        if (medianNormalised <= 0.0)
        {
            // The background is the darkest value in the frame -- see the
            // eight-bit case in this method's doc comment. One output level of
            // headroom below it, and no more.
            low = minimum - (span / (OutputLevels - 1));
            span = maximum - low;
            samples = Sample(image, low, span);
            medianNormalised = MedianOf(samples);
        }

        double madNormalised = MedianAbsoluteDeviation(samples, medianNormalised);
        StretchCurve curve = CurveFor(medianNormalised, madNormalised, Quantile(samples, HighlightAnchorQuantile), target);

        var statistics = new StretchStatistics(
            minimum,
            maximum,
            low + (medianNormalised * span),
            madNormalised * span,
            low,
            curve.Midtone,
            curve);

        return Render(image, low, span, curve, maximumWidth, maximumHeight, statistics);
    }

    /// <summary>
    /// A faithful preview: the full range mapped straight onto the output, no
    /// stretch at all.
    ///
    /// No longer reachable from the application, which always stretches -- an
    /// unstretched astronomical exposure is a black rectangle, and offering one
    /// was offering a broken picture as a choice. It is kept because it is the
    /// evidence for that: the test that a linear rendering of a realistic frame
    /// comes out below 6/255 is what justifies the stretch being the only mode,
    /// and it needs something to render linearly.
    /// </summary>
    public static ImagePreview CreateLinear(FitsImage image, int maximumWidth = 1024, int maximumHeight = 1024)
    {
        ArgumentNullException.ThrowIfNull(image);

        (double minimum, double maximum) = Range(image);

        if (maximum - minimum <= 0.0)
        {
            return Uniform(image, minimum, maximum, maximumWidth, maximumHeight);
        }

        // The same full-range normalisation the stretched path uses, with the
        // identity curve. The two modes differ by the curve alone, not by where
        // black and white sit.
        double span = maximum - minimum;
        double[] samples = Sample(image, minimum, span);
        double medianNormalised = MedianOf(samples);
        double madNormalised = MedianAbsoluteDeviation(samples, medianNormalised);

        var statistics = new StretchStatistics(
            minimum,
            maximum,
            minimum + (medianNormalised * span),
            madNormalised * span,
            minimum,
            0.5,
            StretchCurve.Linear);

        return Render(image, minimum, span, StretchCurve.Linear, maximumWidth, maximumHeight, statistics);
    }

    /// <summary>
    /// Places the two segments: where the core ends, how much output the
    /// highlights keep, and the midtone that puts the background on target.
    ///
    /// All three are decided from the frame's own statistics, which is why an
    /// over-exposed frame and a correctly exposed one both come out usable from
    /// the same code. On the over-exposed frame the anchor lands at three
    /// quarters of the range and frees a sixth of the output; on a frame whose
    /// sky is a fraction of a percent above bias the anchor lands just above the
    /// sky and the slope floor stops it flattening the stars.
    /// </summary>
    public static StretchCurve CurveFor(
        double medianNormalised, double madNormalised, double highQuantile, double targetBackground)
    {
        double background = Math.Clamp(medianNormalised, 0.0, 1.0);
        double target = Math.Clamp(targetBackground, 0.01, 0.9);
        double sigma = Math.Max(0.0, madNormalised) * MadToSigma;

        double anchor = Math.Clamp(
            Math.Max(highQuantile, background + (HighlightAnchorSigmas * sigma)),
            Math.Min(background + 1e-6, 1.0),
            1.0);

        double output = Math.Clamp(
            Math.Max(MinimumHighlightOutput, MinimumHighlightSlope * (1.0 - anchor)),
            0.0,
            1.0 - anchor);

        double ceiling = Math.Max(1.0 - output, Math.Min(0.99, target + TargetHeadroom));

        // The core is asked for the background as a fraction of its own domain
        // and its own output, so the midtone solves the same equation it always
        // did -- just on the segment rather than on the whole range.
        double midtone = Midtone(background / anchor, target / ceiling);

        return new StretchCurve(anchor, ceiling, midtone);
    }

    /// <summary>
    /// The midtone balance that sends <paramref name="normalisedMedian"/> to
    /// <paramref name="targetBackground"/>.
    ///
    /// Derived rather than searched for: setting the transfer function equal to
    /// the target and solving for the midtone gives
    /// <c>m = x(1-t) / (x - 2tx + t)</c>, which is exact and needs no iteration.
    /// </summary>
    public static double Midtone(double normalisedMedian, double targetBackground)
    {
        double x = Math.Clamp(normalisedMedian, 0.0, 1.0);
        double t = Math.Clamp(targetBackground, 0.001, 0.999);

        double denominator = x - (2.0 * t * x) + t;
        if (Math.Abs(denominator) < 1e-12)
        {
            return 0.5;
        }

        return Math.Clamp(x * (1.0 - t) / denominator, 1e-6, 1.0 - 1e-6);
    }

    /// <summary>
    /// The midtone transfer function. Maps 0 to 0, 1 to 1, and the midtone
    /// itself to a half, monotonically in between -- so it brightens the
    /// shadows without clipping the highlights or reordering any two pixels.
    /// </summary>
    public static double ApplyMidtone(double midtone, double value)
    {
        double x = Math.Clamp(value, 0.0, 1.0);

        if (x <= 0.0)
        {
            return 0.0;
        }

        if (x >= 1.0)
        {
            return 1.0;
        }

        double m = Math.Clamp(midtone, 1e-6, 1.0 - 1e-6);
        double denominator = ((2.0 * m) - 1.0) * x - m;

        return Math.Abs(denominator) < 1e-12 ? x : Math.Clamp((m - 1.0) * x / denominator, 0.0, 1.0);
    }

    /// <summary>
    /// Expands a preview to 32-bit BGRA, which every UI toolkit can display
    /// without negotiation. Kept here rather than in the application so the
    /// pixel loop sits in a tested assembly.
    /// </summary>
    public static byte[] ToBgra(ImagePreview preview)
    {
        ArgumentNullException.ThrowIfNull(preview);

        byte[] bgra = new byte[preview.Width * preview.Height * 4];

        for (int i = 0; i < preview.Gray8.Length; i++)
        {
            byte gray = preview.Gray8[i];
            int at = i * 4;
            bgra[at] = gray;
            bgra[at + 1] = gray;
            bgra[at + 2] = gray;
            bgra[at + 3] = 255;
        }

        return bgra;
    }

    private static ImagePreview Render(
        FitsImage image,
        double blackPointAdu,
        double span,
        StretchCurve curve,
        int maximumWidth,
        int maximumHeight,
        StretchStatistics statistics)
    {
        int decimation = Decimation(image.Width, image.Height, maximumWidth, maximumHeight);
        int width = (image.Width + decimation - 1) / decimation;
        int height = (image.Height + decimation - 1) / decimation;

        byte[] gray = new byte[width * height];

        // Precomputed lookup over the transfer function, which is expensive
        // enough per pixel to matter on a five megapixel frame.
        //
        // The size is not arbitrary and 4096 was not enough. The input axis is
        // linear in ADU while everything worth seeing is crowded into its first
        // fraction of a percent: on a realistic frame the sky sits about 0.13%
        // of the way from the darkest pixel to the brightest, so a 4096-entry
        // table resolved the entire noise distribution -- the whole background,
        // and any unevenness in it -- into five steps, and the preview showed
        // six distinct grey levels below the sky where it should show dozens.
        // 65536 gives 16-bit data one entry per ADU, which is as fine as the
        // data itself, for 64 KB and a table build of well under a millisecond.
        const int LookupSize = 65536;
        byte[] lookup = new byte[LookupSize];
        for (int i = 0; i < LookupSize; i++)
        {
            double normalised = i / (double)(LookupSize - 1);
            lookup[i] = (byte)Math.Clamp(Math.Round(curve.Apply(normalised) * (OutputLevels - 1)), 0.0, OutputLevels - 1);
        }

        for (int outputRow = 0; outputRow < height; outputRow++)
        {
            // Flipped: FITS stores its first row at the bottom of the image, and
            // a preview shown upside down relative to the solved orientation
            // would be worse than no preview.
            int sourceRowStart = image.Height - 1 - (outputRow * decimation);

            for (int outputColumn = 0; outputColumn < width; outputColumn++)
            {
                int sourceColumnStart = outputColumn * decimation;

                // Brightest pixel in the block, not the average. A star a pixel
                // or two across is the whole point of the preview, and
                // averaging a 3x3 block would dilute it towards the background
                // while subsampling would miss it entirely.
                double peak = double.NegativeInfinity;
                for (int dy = 0; dy < decimation; dy++)
                {
                    int y = sourceRowStart - dy;
                    if (y < 0)
                    {
                        break;
                    }

                    for (int dx = 0; dx < decimation; dx++)
                    {
                        int x = sourceColumnStart + dx;
                        if (x >= image.Width)
                        {
                            break;
                        }

                        double value = image.Pixels[y, x];
                        if (value > peak)
                        {
                            peak = value;
                        }
                    }
                }

                double normalised = double.IsNegativeInfinity(peak) ? 0.0 : (peak - blackPointAdu) / span;
                int index = (int)Math.Clamp(Math.Round(normalised * (LookupSize - 1)), 0.0, LookupSize - 1);
                gray[(outputRow * width) + outputColumn] = lookup[index];
            }
        }

        return new ImagePreview(width, height, image.Width, image.Height, decimation, gray, statistics);
    }

    private static ImagePreview Uniform(
        FitsImage image, double minimum, double maximum, int maximumWidth, int maximumHeight)
    {
        int decimation = Decimation(image.Width, image.Height, maximumWidth, maximumHeight);
        int width = (image.Width + decimation - 1) / decimation;
        int height = (image.Height + decimation - 1) / decimation;

        byte[] gray = new byte[width * height];
        Array.Fill(gray, (byte)128);

        return new ImagePreview(
            width, height, image.Width, image.Height, decimation, gray,
            new StretchStatistics(minimum, maximum, minimum, 0.0, minimum, 0.5, StretchCurve.Linear));
    }

    private static int Decimation(int width, int height, int maximumWidth, int maximumHeight)
    {
        int horizontal = maximumWidth > 0 ? (width + maximumWidth - 1) / maximumWidth : 1;
        int vertical = maximumHeight > 0 ? (height + maximumHeight - 1) / maximumHeight : 1;
        return Math.Max(1, Math.Max(horizontal, vertical));
    }

    private static (double Minimum, double Maximum) Range(FitsImage image)
    {
        double minimum = double.PositiveInfinity;
        double maximum = double.NegativeInfinity;

        for (int y = 0; y < image.Height; y++)
        {
            for (int x = 0; x < image.Width; x++)
            {
                double value = image.Pixels[y, x];
                if (!double.IsFinite(value))
                {
                    continue;
                }

                if (value < minimum)
                {
                    minimum = value;
                }

                if (value > maximum)
                {
                    maximum = value;
                }
            }
        }

        return double.IsFinite(minimum) && double.IsFinite(maximum) ? (minimum, maximum) : (0.0, 0.0);
    }

    private static double MedianAbsoluteDeviation(double[] sortedSamples, double median)
    {
        if (sortedSamples.Length == 0)
        {
            return 0.0;
        }

        double[] deviations = new double[sortedSamples.Length];
        for (int i = 0; i < sortedSamples.Length; i++)
        {
            deviations[i] = Math.Abs(sortedSamples[i] - median);
        }

        Array.Sort(deviations);
        return MedianOf(deviations);
    }

    /// <summary>Samples the frame on a stride, normalised to the black point and span, and sorted.</summary>
    private static double[] Sample(FitsImage image, double minimum, double span)
    {
        int rows = (image.Height + StatisticsStride - 1) / StatisticsStride;
        int columns = (image.Width + StatisticsStride - 1) / StatisticsStride;

        var samples = new List<double>(rows * columns);

        for (int y = 0; y < image.Height; y += StatisticsStride)
        {
            for (int x = 0; x < image.Width; x += StatisticsStride)
            {
                double value = image.Pixels[y, x];
                if (double.IsFinite(value))
                {
                    samples.Add((value - minimum) / span);
                }
            }
        }

        double[] sorted = samples.ToArray();
        Array.Sort(sorted);
        return sorted;
    }

    private static double Quantile(double[] sortedSamples, double fraction)
    {
        if (sortedSamples.Length == 0)
        {
            return 1.0;
        }

        int at = (int)Math.Clamp(Math.Round(fraction * (sortedSamples.Length - 1)), 0, sortedSamples.Length - 1);
        return sortedSamples[at];
    }

    private static double MedianOf(double[] sortedValues)
    {
        if (sortedValues.Length == 0)
        {
            return 0.0;
        }

        int middle = sortedValues.Length / 2;
        return sortedValues.Length % 2 == 1
            ? sortedValues[middle]
            : 0.5 * (sortedValues[middle - 1] + sortedValues[middle]);
    }
}
