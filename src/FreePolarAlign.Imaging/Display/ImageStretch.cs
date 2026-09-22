using FreePolarAlign.Imaging.Fits;

namespace FreePolarAlign.Imaging.Display;

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
/// The midtone balance handed to the transfer function. Small values brighten
/// hard; 0.5 is no change. This is the only thing the brightness control
/// changes -- the black and white points stay at the frame's own limits.
/// </param>
public sealed record StretchStatistics(
    double MinimumAdu,
    double MaximumAdu,
    double MedianAdu,
    double MadAdu,
    double BlackPointAdu,
    double Midtone);

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
/// The transform is the standard midtone transfer function, placed from the
/// median, which is a robust statistic and that is the point: a mean would be
/// dragged around by the stars, which here are the outliers rather than the
/// thing being measured.
///
/// It is the *only* thing applied. The frame's own darkest and brightest pixels
/// are the black and white points, so the output range is used fully and no
/// pixel is crushed to 0 or blown to 255 unless it genuinely is the darkest or
/// brightest one in the frame. An earlier version clipped everything below
/// median - 2.8 sigma to black, which threw away the bottom of the noise
/// distribution -- a quarter of a percent of pixels on a Gaussian background,
/// and visibly more wherever the sky was not flat. Since one of the things a
/// person looks at this preview to judge is whether the background is even, a
/// display that flattened the dark end was answering that question wrongly.
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

    /// <summary>Distinct levels an eight-bit output has, which is what the preview renders to.</summary>
    private const int OutputLevels = 256;

    /// <summary>
    /// Statistics are taken from every fourth pixel on each axis. A sixteenth of
    /// a five megapixel frame is still 350,000 samples, which pins a median far
    /// tighter than a display decision needs, and it keeps the whole preview
    /// inside a few tens of milliseconds.
    /// </summary>
    private const int StatisticsStride = 4;

    /// <summary>
    /// Builds a preview using the frame's full range -- darkest pixel to black,
    /// brightest to white -- with the background placed at
    /// <paramref name="targetBackground"/> by the midtone transfer function.
    ///
    /// Nothing is clipped at either end. Every pixel strictly between the
    /// darkest and the brightest lands strictly between 0 and 255, because the
    /// only transform applied is monotonic and fixes both endpoints. A pixel
    /// renders as 0 exactly when it is the darkest in the frame, and 255 exactly
    /// when it is the brightest.
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
        double medianNormalised = Median(image, low, span);

        if (medianNormalised <= 0.0)
        {
            // The background is the darkest value in the frame -- see the
            // eight-bit case in this method's doc comment. One output level of
            // headroom below it, and no more.
            low = minimum - (span / (OutputLevels - 1));
            span = maximum - low;
            medianNormalised = Median(image, low, span);
        }

        double madNormalised = MedianAbsoluteDeviation(image, low, span, medianNormalised);
        double midtone = Midtone(medianNormalised, target);

        var statistics = new StretchStatistics(
            minimum,
            maximum,
            low + (medianNormalised * span),
            madNormalised * span,
            low,
            midtone);

        return Render(image, low, span, midtone, maximumWidth, maximumHeight, statistics);
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
        // midtone left at a half, which is the identity. The two modes differ by
        // the curve alone, not by where black and white sit.
        double span = maximum - minimum;
        double medianNormalised = Median(image, minimum, span);
        double madNormalised = MedianAbsoluteDeviation(image, minimum, span, medianNormalised);

        var statistics = new StretchStatistics(
            minimum, maximum, minimum + (medianNormalised * span), madNormalised * span, minimum, 0.5);

        return Render(image, minimum, span, midtone: 0.5, maximumWidth, maximumHeight, statistics);
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
        double midtone,
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
            lookup[i] = (byte)Math.Clamp(Math.Round(ApplyMidtone(midtone, normalised) * (OutputLevels - 1)), 0.0, OutputLevels - 1);
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
            new StretchStatistics(minimum, maximum, minimum, 0.0, minimum, 0.5));
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

    private static double Median(FitsImage image, double minimum, double span)
    {
        double[] samples = Sample(image, minimum, span);
        return samples.Length == 0 ? 0.0 : MedianOf(samples);
    }

    private static double MedianAbsoluteDeviation(FitsImage image, double minimum, double span, double median)
    {
        double[] samples = Sample(image, minimum, span);
        if (samples.Length == 0)
        {
            return 0.0;
        }

        for (int i = 0; i < samples.Length; i++)
        {
            samples[i] = Math.Abs(samples[i] - median);
        }

        return MedianOf(samples);
    }

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

        return samples.ToArray();
    }

    private static double MedianOf(double[] values)
    {
        Array.Sort(values);

        int middle = values.Length / 2;
        return values.Length % 2 == 1
            ? values[middle]
            : 0.5 * (values[middle - 1] + values[middle]);
    }
}
