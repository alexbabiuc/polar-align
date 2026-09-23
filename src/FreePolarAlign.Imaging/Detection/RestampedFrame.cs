using FreePolarAlign.Imaging.Fits;

namespace FreePolarAlign.Imaging.Detection;

/// <summary>
/// Draws a frame containing nothing but the stars a detector found in another
/// one: same pixel positions, same brightness order, a flat background and no
/// noise.
///
/// The point is everything it leaves out. A real exposure carries a sky
/// gradient, read noise, amp glow, hot pixels, satellite trails and whatever
/// else was in front of the telescope, and a plate solver's own detector has to
/// find stars through all of it. Handing it a picture of just the stars asks a
/// narrower question -- "do these points match the sky?" -- of exactly the data
/// this project already trusts enough to centroid.
///
/// This is a rendering of a measurement, never a substitute for the frame. It
/// can only lose information: a star the detector missed is not here, and a
/// spurious detection is drawn as confidently as a real one. Nothing measured
/// should ever be computed from these pixels -- they exist to be matched
/// against a catalogue and then thrown away.
/// </summary>
public static class RestampedFrame
{
    /// <summary>
    /// Sky level of the drawn frame. Not zero: a solver estimates a background
    /// before thresholding, and a frame whose background is exactly the minimum
    /// representable value is an odd thing to ask one to measure.
    /// </summary>
    private const double BackgroundLevel = 500.0;

    /// <summary>
    /// Gaussian width of the drawn stars, in pixels. Comfortably sampled -- a
    /// star drawn onto one pixel gives a centroider nothing to work with, and
    /// the whole exercise depends on the positions surviving the round trip.
    /// </summary>
    private const double StarSigmaPixels = 1.6;

    /// <summary>Radius drawn around each star, in pixels: past three sigma the profile is below the noise of the original frame anyway.</summary>
    private const int StampRadius = 5;

    private const double FaintestPeak = 2000.0;

    private const double BrightestPeak = 60000.0;

    /// <summary>
    /// Compresses the measured flux range onto the drawn brightness range. Real
    /// frames span several magnitudes and a linear map would leave everything
    /// but the brightest few stars indistinguishable from the background, while
    /// the order -- which is what a matcher uses to choose its quads -- is all
    /// that has to survive.
    /// </summary>
    private const double BrightnessCompression = 0.4;

    /// <param name="stars">
    /// Detected stars, positions 1-based as <see cref="DetectedStar"/> reports
    /// them. Order is irrelevant; brightness comes from
    /// <see cref="DetectedStar.Flux"/>.
    /// </param>
    public static FitsImage Render(int width, int height, IReadOnlyList<DetectedStar> stars)
    {
        ArgumentNullException.ThrowIfNull(stars);
        ArgumentOutOfRangeException.ThrowIfLessThan(width, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(height, 1);

        var pixels = new double[height, width];
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                pixels[y, x] = BackgroundLevel;
            }
        }

        double brightest = 0.0;
        foreach (DetectedStar star in stars)
        {
            if (star.Flux > brightest)
            {
                brightest = star.Flux;
            }
        }

        if (brightest <= 0.0)
        {
            return FitsImage.ForCapturedFrame(width, height, pixels, bitsPerPixel: 16);
        }

        foreach (DetectedStar star in stars)
        {
            if (star.Flux <= 0.0)
            {
                continue;
            }

            double peak = FaintestPeak +
                ((BrightestPeak - FaintestPeak) * Math.Pow(star.Flux / brightest, BrightnessCompression));

            // The detector counts from one, the pixel array from zero.
            double centreX = star.X - 1.0;
            double centreY = star.Y - 1.0;

            int nearestX = (int)Math.Round(centreX);
            int nearestY = (int)Math.Round(centreY);

            for (int dy = -StampRadius; dy <= StampRadius; dy++)
            {
                int y = nearestY + dy;
                if (y < 0 || y >= height)
                {
                    continue;
                }

                for (int dx = -StampRadius; dx <= StampRadius; dx++)
                {
                    int x = nearestX + dx;
                    if (x < 0 || x >= width)
                    {
                        continue;
                    }

                    // Offsets from the *centroid*, not from the nearest pixel,
                    // so the sub-pixel position the detector measured is what
                    // the solver gets back.
                    double offsetX = x - centreX;
                    double offsetY = y - centreY;
                    double profile = Math.Exp(
                        -((offsetX * offsetX) + (offsetY * offsetY)) / (2.0 * StarSigmaPixels * StarSigmaPixels));

                    pixels[y, x] = Math.Min(65535.0, pixels[y, x] + (peak * profile));
                }
            }
        }

        return FitsImage.ForCapturedFrame(width, height, pixels, bitsPerPixel: 16);
    }
}
