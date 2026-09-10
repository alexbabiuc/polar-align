using FreePolarAlign.Imaging.Fits;

namespace FreePolarAlign.Imaging.Display;

/// <param name="MedianAdu">Background level, in the image's own units. The single most useful number for "is this frame any good".</param>
/// <param name="MadAdu">
/// Median absolute deviation of the pixels. A robust noise estimate: unlike a
/// standard deviation it is barely moved by the stars, which are the outliers
/// here rather than the signal being characterised.
/// </param>
/// <param name="BlackPoint">Normalised value mapped to black, below which everything is clipped.</param>
/// <param name="Midtone">
/// The midtone balance handed to the transfer function. Small values brighten
/// hard; 0.5 is no change.
/// </param>
/// <param name="NormalisationLowAdu">
/// The value treated as zero before the transform is applied -- normally zero
/// itself, and the observed minimum only when the data contains negative values.
///
/// Deliberately *not* the observed minimum for ordinary data, which was the
/// first attempt and was wrong. Normalising from the darkest pixel present means
/// that whenever the background happens to be the darkest value -- which is what
/// an eight-bit readout produces, because it quantises the noise away entirely --
/// the background sits at exactly zero, and the transfer function fixes zero at
/// zero. The stretch then cannot lift it and the frame renders black however far
/// the slider is dragged.
/// </param>
public sealed record StretchStatistics(
    double MinimumAdu,
    double MaximumAdu,
    double MedianAdu,
    double MadAdu,
    double BlackPoint,
    double Midtone,
    double NormalisationLowAdu);

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
/// median and the median absolute deviation. Both are robust statistics, and
/// that is the point: a mean and a standard deviation would be dragged around by
/// the stars, which here are the outliers rather than the thing being measured.
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
    /// How far below the median, in noise units, is treated as black. Just under
    /// three sigma clips the bottom of the noise distribution without eating
    /// into it: a preview that clipped the noise flat would hide exactly the
    /// gradient a user is looking for when they suspect dew, twilight or a light
    /// leak.
    /// </summary>
    public const double DefaultShadowClipping = 2.8;

    /// <summary>Scales a median absolute deviation to a standard-deviation-equivalent for Gaussian noise.</summary>
    private const double MadToSigma = 1.4826;

    /// <summary>
    /// Statistics are taken from every fourth pixel on each axis. A sixteenth of
    /// a five megapixel frame is still 350,000 samples, which pins a median far
    /// tighter than a display decision needs, and it keeps the whole preview
    /// inside a few tens of milliseconds.
    /// </summary>
    private const int StatisticsStride = 4;

    /// <summary>
    /// Builds a preview with the background placed at
    /// <paramref name="targetBackground"/>.
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

        double low = NormalisationLow(minimum);
        double span = maximum - low;

        double medianNormalised = Median(image, low, span);
        double madNormalised = MedianAbsoluteDeviation(image, low, span, medianNormalised);

        double blackPoint = madNormalised > 0.0
            ? Math.Clamp(medianNormalised - (DefaultShadowClipping * MadToSigma * madNormalised), 0.0, 0.999)
            : 0.0;

        double medianAfterBlackPoint = (medianNormalised - blackPoint) / (1.0 - blackPoint);
        double midtone = Midtone(medianAfterBlackPoint, target);

        var statistics = new StretchStatistics(
            minimum,
            maximum,
            low + (medianNormalised * span),
            madNormalised * span,
            blackPoint,
            midtone,
            low);

        return Render(image, low, span, blackPoint, midtone, maximumWidth, maximumHeight, statistics);
    }

    /// <summary>
    /// A faithful preview: the full range mapped straight onto the output, no
    /// stretch at all. Almost always shows very little, and is offered so a user
    /// can see what the stretch is doing rather than wonder.
    /// </summary>
    public static ImagePreview CreateLinear(FitsImage image, int maximumWidth = 1024, int maximumHeight = 1024)
    {
        ArgumentNullException.ThrowIfNull(image);

        (double minimum, double maximum) = Range(image);

        if (maximum - minimum <= 0.0)
        {
            return Uniform(image, minimum, maximum, maximumWidth, maximumHeight);
        }

        // A faithful rendering shows the data as it is, so it normalises across
        // the observed range rather than from zero: the point of this mode is to
        // show what the frame really looks like, not to be kind to it.
        double span = maximum - minimum;
        double medianNormalised = Median(image, minimum, span);
        double madNormalised = MedianAbsoluteDeviation(image, minimum, span, medianNormalised);

        var statistics = new StretchStatistics(
            minimum, maximum, minimum + (medianNormalised * span), madNormalised * span, 0.0, 0.5, minimum);

        return Render(image, minimum, span, blackPoint: 0.0, midtone: 0.5, maximumWidth, maximumHeight, statistics);
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
        double minimum,
        double span,
        double blackPoint,
        double midtone,
        int maximumWidth,
        int maximumHeight,
        StretchStatistics statistics)
    {
        int decimation = Decimation(image.Width, image.Height, maximumWidth, maximumHeight);
        int width = (image.Width + decimation - 1) / decimation;
        int height = (image.Height + decimation - 1) / decimation;

        byte[] gray = new byte[width * height];

        // Precomputed lookup over the transfer function. The function is
        // expensive enough per pixel to matter on a five megapixel frame and
        // monotonic, so 4096 steps are indistinguishable from evaluating it
        // everywhere.
        const int LookupSize = 4096;
        byte[] lookup = new byte[LookupSize];
        for (int i = 0; i < LookupSize; i++)
        {
            double normalised = i / (double)(LookupSize - 1);
            double afterBlackPoint = blackPoint >= 1.0 ? 0.0 : (normalised - blackPoint) / (1.0 - blackPoint);
            lookup[i] = (byte)Math.Clamp(Math.Round(ApplyMidtone(midtone, afterBlackPoint) * 255.0), 0.0, 255.0);
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

                double normalised = double.IsNegativeInfinity(peak) ? 0.0 : (peak - minimum) / span;
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
            new StretchStatistics(minimum, maximum, minimum, 0.0, 0.0, 0.5, minimum));
    }

    /// <summary>
    /// Where zero sits for the purpose of the stretch: the origin for ordinary
    /// sensor data, and the observed minimum only when the frame contains
    /// negative values, as a bias-subtracted or calibrated float frame can.
    /// </summary>
    private static double NormalisationLow(double minimum) => Math.Min(0.0, minimum);

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
