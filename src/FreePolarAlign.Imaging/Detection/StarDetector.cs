using FreePolarAlign.Imaging.Fits;

namespace FreePolarAlign.Imaging.Detection;

/// <summary>
/// One detected source. Coordinates are 1-based, matching the FITS convention
/// that CRPIX uses, so a centroid can be handed to
/// <see cref="Wcs.TanWcsSolution"/> without an off-by-one adjustment.
/// </summary>
/// <param name="FwhmPixels">
/// Full width at half maximum from the second moments, assuming a Gaussian
/// profile. Grows with poor focus and with seeing, so it is the headline
/// image-quality number.
/// </param>
/// <param name="Elongation">
/// Ratio of the major to minor axis of the source's second-moment ellipse. One
/// for a round star; greater than one for a trailed or drifting one. This is
/// what distinguishes bad tracking from bad focus, since defocus inflates
/// <see cref="FwhmPixels"/> while staying round.
/// </param>
/// <param name="PositionAngleDegrees">
/// Orientation of the elongation, degrees anticlockwise from the pixel x axis.
/// Meaningless for a round source; useful for trails, because a genuine
/// tracking error elongates every star in the same direction while noise does
/// not.
/// </param>
public sealed record DetectedStar(
    double X,
    double Y,
    double Flux,
    double PeakAboveBackground,
    double FwhmPixels,
    double Elongation,
    double PositionAngleDegrees,
    int PixelCount);

/// <summary>
/// What the frame itself looks like, independent of any solve. Reported so that
/// a failure can be explained in terms a user can act on -- "the frame is
/// clouded", "the stars are trailed" -- rather than only as "no solution"
/// (Phase 2's requirement that degraded inputs fail cleanly with a useful
/// message).
/// </summary>
/// <param name="MedianElongation">
/// Median elongation of the brighter sources. A frame where most stars are
/// elongated in a common direction is trailed, whatever the solver then says.
/// </param>
/// <param name="BackgroundGradientFraction">
/// Spread of the tiled background estimate relative to its own median. Large
/// values mean an uneven sky -- cloud, moon, or light pollution gradient --
/// which is worth reporting even when enough stars survive to solve.
/// </param>
public sealed record FrameStatistics(
    double BackgroundLevel,
    double NoiseLevel,
    double BackgroundGradientFraction,
    int StarCount,
    double MedianFwhmPixels,
    double MedianElongation,
    double SaturatedFraction);

/// <summary>
/// Finds and centroids stars in a frame.
///
/// The background is estimated on a coarse tile grid and interpolated rather
/// than taken as one number for the whole frame. That matters for the degraded
/// inputs this phase has to survive: cloud, moonlight and light-pollution
/// gradients all put a slope across the frame, and a single global threshold
/// then either drowns the faint half of the image or floods the bright half with
/// spurious detections.
///
/// Noise is measured by median absolute deviation, not standard deviation,
/// because the stars themselves are outliers in the very distribution being
/// characterised, and a handful of bright ones is enough to inflate an ordinary
/// standard deviation into uselessness.
/// </summary>
public static class StarDetector
{
    private const double MadToSigma = 1.4826;

    /// <param name="detectionThresholdSigma">
    /// Detection threshold above the local background, in noise units. Five is
    /// conservative: at three, a megapixel frame yields thousands of noise
    /// peaks, and feeding those to a solver wastes far more time than the few
    /// genuine faint stars they hide among are worth.
    /// </param>
    /// <param name="minimumPixels">
    /// Smallest connected group counted as a source. Two or fewer adjacent
    /// pixels above threshold is far more often a cosmic ray or hot pixel than
    /// a star, and a hot pixel is worse than useless to a solver because it is
    /// in the same place on every frame.
    /// </param>
    public static IReadOnlyList<DetectedStar> Detect(
        FitsImage image,
        double detectionThresholdSigma = 5.0,
        int minimumPixels = 3,
        int maximumStars = 500)
    {
        ArgumentNullException.ThrowIfNull(image);
        return DetectWithStatistics(image, detectionThresholdSigma, minimumPixels, maximumStars).Stars;
    }

    public static (IReadOnlyList<DetectedStar> Stars, FrameStatistics Statistics) DetectWithStatistics(
        FitsImage image,
        double detectionThresholdSigma = 5.0,
        int minimumPixels = 3,
        int maximumStars = 500)
    {
        ArgumentNullException.ThrowIfNull(image);

        int width = image.Width;
        int height = image.Height;
        double[,] pixels = image.Pixels;

        var (background, noise, gradientFraction, medianBackground, medianNoise) = EstimateBackground(pixels, width, height);

        double saturationLevel = SaturationLevel(image);
        long saturatedPixels = 0;

        var visited = new bool[height, width];
        var stars = new List<DetectedStar>();
        var queueX = new List<int>();
        var queueY = new List<int>();

        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                if (visited[y, x])
                {
                    continue;
                }

                double threshold = background[y, x] + detectionThresholdSigma * noise[y, x];
                if (pixels[y, x] <= threshold)
                {
                    visited[y, x] = true;
                    continue;
                }

                queueX.Clear();
                queueY.Clear();
                FloodFill(pixels, background, noise, visited, width, height, x, y, detectionThresholdSigma, queueX, queueY);

                if (queueX.Count < minimumPixels)
                {
                    continue;
                }

                DetectedStar? star = Measure(pixels, background, queueX, queueY, saturationLevel, out long saturatedInSource);
                saturatedPixels += saturatedInSource;
                if (star is not null)
                {
                    stars.Add(star);
                }
            }
        }

        stars.Sort((a, b) => b.Flux.CompareTo(a.Flux));
        if (stars.Count > maximumStars)
        {
            stars.RemoveRange(maximumStars, stars.Count - maximumStars);
        }

        var statistics = new FrameStatistics(
            medianBackground,
            medianNoise,
            gradientFraction,
            stars.Count,
            Median(stars.Select(s => s.FwhmPixels).ToList()),
            // Only the brighter half: faint sources have noisy second moments,
            // and including them would make every frame look mildly trailed.
            Median(stars.Take(Math.Max(1, stars.Count / 2)).Select(s => s.Elongation).ToList()),
            (double)saturatedPixels / ((long)width * height));

        return (stars, statistics);
    }

    /// <summary>
    /// Tiled median/MAD background, bilinearly interpolated back to full
    /// resolution. Tiles are sized so that a tile is much larger than a star but
    /// much smaller than the scale a sky gradient varies on.
    /// </summary>
    private static (double[,] Background, double[,] Noise, double GradientFraction, double MedianBackground, double MedianNoise)
        EstimateBackground(double[,] pixels, int width, int height)
    {
        const int targetTiles = 8;
        int tileWidth = Math.Max(16, width / targetTiles);
        int tileHeight = Math.Max(16, height / targetTiles);
        int tilesX = Math.Max(1, (int)Math.Ceiling((double)width / tileWidth));
        int tilesY = Math.Max(1, (int)Math.Ceiling((double)height / tileHeight));

        var tileBackground = new double[tilesY, tilesX];
        var tileNoise = new double[tilesY, tilesX];
        var buffer = new List<double>(tileWidth * tileHeight);

        for (int ty = 0; ty < tilesY; ty++)
        {
            for (int tx = 0; tx < tilesX; tx++)
            {
                buffer.Clear();
                int x0 = tx * tileWidth, x1 = Math.Min(width, x0 + tileWidth);
                int y0 = ty * tileHeight, y1 = Math.Min(height, y0 + tileHeight);
                for (int y = y0; y < y1; y++)
                {
                    for (int x = x0; x < x1; x++)
                    {
                        buffer.Add(pixels[y, x]);
                    }
                }

                double median = Median(buffer);
                tileBackground[ty, tx] = median;

                var deviations = new List<double>(buffer.Count);
                foreach (double value in buffer)
                {
                    deviations.Add(Math.Abs(value - median));
                }

                tileNoise[ty, tx] = Math.Max(Median(deviations) * MadToSigma, 1e-9);
            }
        }

        var background = new double[height, width];
        var noise = new double[height, width];
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                background[y, x] = BilinearSample(tileBackground, tilesX, tilesY, tileWidth, tileHeight, x, y);
                noise[y, x] = BilinearSample(tileNoise, tilesX, tilesY, tileWidth, tileHeight, x, y);
            }
        }

        var flatBackground = new List<double>(tilesX * tilesY);
        var flatNoise = new List<double>(tilesX * tilesY);
        for (int ty = 0; ty < tilesY; ty++)
        {
            for (int tx = 0; tx < tilesX; tx++)
            {
                flatBackground.Add(tileBackground[ty, tx]);
                flatNoise.Add(tileNoise[ty, tx]);
            }
        }

        double medianBackground = Median(flatBackground);
        double medianNoise = Median(flatNoise);
        double gradientFraction = flatBackground.Count > 1 && Math.Abs(medianBackground) > 1e-9
            ? (flatBackground.Max() - flatBackground.Min()) / Math.Abs(medianBackground)
            : 0.0;

        return (background, noise, gradientFraction, medianBackground, medianNoise);
    }

    private static double BilinearSample(double[,] tiles, int tilesX, int tilesY, int tileWidth, int tileHeight, int x, int y)
    {
        double fx = ((double)x - tileWidth / 2.0) / tileWidth;
        double fy = ((double)y - tileHeight / 2.0) / tileHeight;

        int ix = (int)Math.Floor(fx);
        int iy = (int)Math.Floor(fy);
        double dx = fx - ix;
        double dy = fy - iy;

        int x0 = Math.Clamp(ix, 0, tilesX - 1);
        int x1 = Math.Clamp(ix + 1, 0, tilesX - 1);
        int y0 = Math.Clamp(iy, 0, tilesY - 1);
        int y1 = Math.Clamp(iy + 1, 0, tilesY - 1);

        double top = tiles[y0, x0] * (1 - dx) + tiles[y0, x1] * dx;
        double bottom = tiles[y1, x0] * (1 - dx) + tiles[y1, x1] * dx;
        return top * (1 - dy) + bottom * dy;
    }

    private static void FloodFill(
        double[,] pixels, double[,] background, double[,] noise, bool[,] visited,
        int width, int height, int seedX, int seedY, double thresholdSigma,
        List<int> outX, List<int> outY)
    {
        var stackX = new Stack<int>();
        var stackY = new Stack<int>();
        stackX.Push(seedX);
        stackY.Push(seedY);
        visited[seedY, seedX] = true;

        while (stackX.Count > 0)
        {
            int x = stackX.Pop();
            int y = stackY.Pop();
            outX.Add(x);
            outY.Add(y);

            for (int dy = -1; dy <= 1; dy++)
            {
                for (int dx = -1; dx <= 1; dx++)
                {
                    int nx = x + dx, ny = y + dy;
                    if (nx < 0 || ny < 0 || nx >= width || ny >= height || visited[ny, nx])
                    {
                        continue;
                    }

                    if (pixels[ny, nx] > background[ny, nx] + thresholdSigma * noise[ny, nx])
                    {
                        visited[ny, nx] = true;
                        stackX.Push(nx);
                        stackY.Push(ny);
                    }
                }
            }
        }
    }

    private static DetectedStar? Measure(
        double[,] pixels, double[,] background,
        List<int> xs, List<int> ys,
        double saturationLevel, out long saturatedPixels)
    {
        saturatedPixels = 0;
        double flux = 0.0, peak = 0.0;
        double sumX = 0.0, sumY = 0.0;

        for (int i = 0; i < xs.Count; i++)
        {
            int x = xs[i], y = ys[i];
            double value = pixels[y, x] - background[y, x];
            if (value <= 0)
            {
                continue;
            }

            if (pixels[y, x] >= saturationLevel)
            {
                saturatedPixels++;
            }

            flux += value;
            sumX += value * x;
            sumY += value * y;
            peak = Math.Max(peak, value);
        }

        if (flux <= 0)
        {
            return null;
        }

        double centroidX = sumX / flux;
        double centroidY = sumY / flux;

        double mxx = 0.0, myy = 0.0, mxy = 0.0;
        for (int i = 0; i < xs.Count; i++)
        {
            int x = xs[i], y = ys[i];
            double value = pixels[y, x] - background[y, x];
            if (value <= 0)
            {
                continue;
            }

            double dx = x - centroidX;
            double dy = y - centroidY;
            mxx += value * dx * dx;
            myy += value * dy * dy;
            mxy += value * dx * dy;
        }

        mxx /= flux;
        myy /= flux;
        mxy /= flux;

        // Moments taken only over above-threshold pixels are truncated, and the
        // truncation is worse for broad, low-peak sources than for sharp ones --
        // so a badly defocused star reads as only slightly wider than a sharp
        // one, which is precisely backwards for a number meant to tell a user
        // their focus is off. Re-measure with a Gaussian window over *all*
        // nearby pixels instead, then divide the window back out.
        var (major, minor, mxy2) = WindowedMoments(pixels, background, centroidX, centroidY, mxx, myy, mxy);

        double fwhm = 2.0 * Math.Sqrt(2.0 * Math.Log(2.0)) * Math.Sqrt(Math.Sqrt(major * minor));
        double elongation = Math.Sqrt(major / minor);
        double positionAngle = 0.5 * Math.Atan2(2.0 * mxy2, mxx - myy) * 180.0 / Math.PI;

        return new DetectedStar(
            centroidX + 1.0,
            centroidY + 1.0,
            flux,
            peak,
            fwhm,
            elongation,
            positionAngle,
            xs.Count);
    }

    /// <summary>
    /// Second moments measured with a Gaussian window, with the window's own
    /// width divided back out.
    ///
    /// Weighting by a Gaussian instead of cutting at the detection threshold
    /// removes the truncation bias, at the cost of measuring the source
    /// convolved with the window. For Gaussian source and window that
    /// convolution is exact and invertible along each principal axis --
    /// 1/sigma_measured^2 = 1/sigma_source^2 + 1/sigma_window^2 -- so the
    /// deconvolution below recovers the true width rather than approximating
    /// it. Real stars are not exactly Gaussian, so this is a good estimator
    /// rather than a perfect one, which is all a focus indicator needs.
    /// </summary>
    private static (double Major, double Minor, double Mxy) WindowedMoments(
        double[,] pixels, double[,] background,
        double centroidX, double centroidY,
        double roughMxx, double roughMyy, double roughMxy)
    {
        static (double Major, double Minor) Eigenvalues(double mxx, double myy, double mxy)
        {
            double halfTrace = 0.5 * (mxx + myy);
            double discriminant = Math.Sqrt(Math.Max(0.25 * (mxx - myy) * (mxx - myy) + mxy * mxy, 0.0));
            return (Math.Max(halfTrace + discriminant, 1e-12), Math.Max(halfTrace - discriminant, 1e-12));
        }

        var (roughMajor, roughMinor) = Eigenvalues(roughMxx, roughMyy, roughMxy);

        // The window has to be comfortably wider than the source or the
        // deconvolution below becomes ill-conditioned; the rough moments
        // understate the source, so this is a lower bound on how wide it needs
        // to be, and the factor buys margin.
        double windowSigma = Math.Max(2.0, 2.5 * Math.Sqrt(Math.Sqrt(roughMajor * roughMinor)));
        double windowVariance = windowSigma * windowSigma;

        int height = pixels.GetLength(0);
        int width = pixels.GetLength(1);
        int radius = (int)Math.Ceiling(3.0 * windowSigma);

        int x0 = Math.Max(0, (int)Math.Floor(centroidX) - radius);
        int x1 = Math.Min(width - 1, (int)Math.Ceiling(centroidX) + radius);
        int y0 = Math.Max(0, (int)Math.Floor(centroidY) - radius);
        int y1 = Math.Min(height - 1, (int)Math.Ceiling(centroidY) + radius);

        double weightedFlux = 0.0, mxx = 0.0, myy = 0.0, mxy = 0.0;
        for (int y = y0; y <= y1; y++)
        {
            double dy = y - centroidY;
            for (int x = x0; x <= x1; x++)
            {
                double dx = x - centroidX;
                double weight = Math.Exp(-(dx * dx + dy * dy) / (2.0 * windowVariance));

                // Deliberately including pixels that came out negative after
                // background subtraction: they are the other half of the noise
                // distribution, and dropping them would bias the widths up.
                double value = (pixels[y, x] - background[y, x]) * weight;
                weightedFlux += value;
                mxx += value * dx * dx;
                myy += value * dy * dy;
                mxy += value * dx * dy;
            }
        }

        if (weightedFlux <= 0)
        {
            return (roughMajor, roughMinor, roughMxy);
        }

        mxx /= weightedFlux;
        myy /= weightedFlux;
        mxy /= weightedFlux;

        var (measuredMajor, measuredMinor) = Eigenvalues(mxx, myy, mxy);

        double Deconvolve(double measured)
        {
            if (measured <= 0 || measured >= windowVariance)
            {
                return measured;
            }

            return measured * windowVariance / (windowVariance - measured);
        }

        double major = Deconvolve(measuredMajor);
        double minor = Deconvolve(measuredMinor);

        return major >= minor && minor > 0
            ? (major, minor, mxy)
            : (roughMajor, roughMinor, roughMxy);
    }

    private static double SaturationLevel(FitsImage image) => image.BitPix switch
    {
        FitsBitPix.Byte => 255.0,
        FitsBitPix.Int16 => 32767.0 * image.Bscale + image.Bzero,
        FitsBitPix.Int32 => int.MaxValue * image.Bscale + image.Bzero,
        _ => double.PositiveInfinity,
    };

    private static double Median(List<double> values)
    {
        if (values.Count == 0)
        {
            return 0.0;
        }

        values.Sort();
        int middle = values.Count / 2;
        return values.Count % 2 == 1
            ? values[middle]
            : 0.5 * (values[middle - 1] + values[middle]);
    }
}
