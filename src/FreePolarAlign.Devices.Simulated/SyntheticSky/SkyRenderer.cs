using FreePolarAlign.Imaging.Fits;
using FreePolarAlign.Imaging.Wcs;

namespace FreePolarAlign.Devices.Simulated.SyntheticSky;

/// <summary>
/// Everything about a frame that is not geometry: the atmosphere, the sensor,
/// and the ways a real capture goes wrong.
/// </summary>
/// <param name="SeeingFwhmPixels">
/// Star profile width. Also the knob for poor focus, which to first order just
/// makes stars broader while leaving them round -- a real defocus is an annulus
/// rather than a wider Gaussian, but the effect that matters here (flux spread
/// over more pixels, so lower peak signal-to-noise and worse centroids) is
/// reproduced faithfully.
/// </param>
/// <param name="ZeropointMagnitude">
/// Magnitude of a star that yields one electron per second. Sets the overall
/// sensitivity of the simulated system.
/// </param>
/// <param name="TrailLengthPixels">
/// Length of star trailing, from tracking error or a slew that had not settled.
/// Distinguishable from defocus because it elongates every star in the same
/// direction.
/// </param>
/// <param name="CloudTransmission">
/// Fraction of starlight getting through, 1 for clear. Attenuates stars while
/// leaving read noise untouched, which is what actually defeats a solve.
/// </param>
/// <param name="CloudGradientFraction">
/// Variation in transmission across the frame, as a fraction. Cloud is rarely
/// uniform, and a gradient is what defeats a naive global detection threshold.
/// </param>
public sealed record ObservingConditions(
    double SeeingFwhmPixels = 3.0,
    double ExposureSeconds = 2.0,
    double SkyElectronsPerSecondPerPixel = 50.0,
    double ReadNoiseElectrons = 8.0,
    double GainElectronsPerAdu = 1.0,
    double ZeropointMagnitude = 20.0,
    double TrailLengthPixels = 0.0,
    double TrailAngleDegrees = 0.0,
    double CloudTransmission = 1.0,
    double CloudGradientFraction = 0.0,
    double BiasAdu = 500.0,
    double MagnitudeLimit = 15.0)
{
    public static readonly ObservingConditions Nominal = new();
}

/// <summary>
/// Renders a synthetic frame from a real star catalogue through a known WCS.
///
/// This is the heart of the virtual observatory: it closes the loop with exact
/// ground truth on a laptop at noon, so every accuracy claim in the roadmap can
/// be verified without waiting for a clear night. Stars are projected with the
/// same <see cref="TanWcsSolution"/> code the solver's output is read back
/// through, and the frame is written as real FITS, so the whole pipeline
/// downstream -- detection, solving, the fit -- sees exactly what it would see
/// from a camera.
/// </summary>
public static class SkyRenderer
{
    /// <summary>
    /// Renders a frame. Returns the image with the true WCS in its header; the
    /// caller is responsible for not letting a solver see it when the point of
    /// the test is a blind solve.
    /// </summary>
    public static FitsImage Render(
        StarCatalog catalog,
        TanWcsSolution wcs,
        int width,
        int height,
        ObservingConditions conditions,
        Random random)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(wcs);
        ArgumentNullException.ThrowIfNull(conditions);
        ArgumentNullException.ThrowIfNull(random);

        var electrons = new double[height, width];

        // Reach beyond the frame corners: a star just outside still spills flux
        // across the boundary, and omitting those would leave the frame edges
        // subtly emptier than the sky really is.
        double fieldRadius = wcs.FieldRadiusDegrees(width, height);
        double margin = 4.0 * conditions.SeeingFwhmPixels + conditions.TrailLengthPixels;
        double searchRadius = fieldRadius + (margin * wcs.PixelScaleArcsecondsPerPixel / 3600.0);

        IReadOnlyList<CatalogStar> stars = catalog.Cone(
            wcs.Crval1Degrees, wcs.Crval2Degrees, searchRadius, conditions.MagnitudeLimit);

        double sigma = conditions.SeeingFwhmPixels / (2.0 * Math.Sqrt(2.0 * Math.Log(2.0)));
        int trailSteps = Math.Max(1, (int)Math.Ceiling(conditions.TrailLengthPixels / 0.5));
        double trailAngle = conditions.TrailAngleDegrees * Math.PI / 180.0;
        double trailDx = Math.Cos(trailAngle) * conditions.TrailLengthPixels;
        double trailDy = Math.Sin(trailAngle) * conditions.TrailLengthPixels;

        foreach (CatalogStar star in stars)
        {
            double x, y;
            try
            {
                (x, y) = wcs.WorldToPixel(star.RaDegrees, star.DecDegrees);
            }
            catch (ArgumentOutOfRangeException)
            {
                // More than 90 degrees from the tangent point: not on this frame.
                continue;
            }

            if (x < -margin || y < -margin || x > width + margin || y > height + margin)
            {
                continue;
            }

            double transmission = Transmission(conditions, x, y, width, height);
            double totalElectrons = conditions.ExposureSeconds * transmission
                * Math.Pow(10.0, -0.4 * (star.Magnitude - conditions.ZeropointMagnitude));

            if (totalElectrons <= 0)
            {
                continue;
            }

            double perStep = totalElectrons / trailSteps;
            for (int step = 0; step < trailSteps; step++)
            {
                double t = trailSteps == 1 ? 0.0 : (step / (double)(trailSteps - 1)) - 0.5;
                AddGaussian(electrons, width, height, x - 1.0 + t * trailDx, y - 1.0 + t * trailDy, perStep, sigma);
            }
        }

        var pixels = new double[height, width];
        double skyElectrons = conditions.SkyElectronsPerSecondPerPixel * conditions.ExposureSeconds;

        for (int row = 0; row < height; row++)
        {
            for (int column = 0; column < width; column++)
            {
                double signal = electrons[row, column] + skyElectrons;
                double measured = PoissonSample(random, signal) + NextGaussian(random) * conditions.ReadNoiseElectrons;
                double adu = conditions.BiasAdu + measured / conditions.GainElectronsPerAdu;
                pixels[row, column] = Math.Clamp(Math.Round(adu), 0.0, 65535.0);
            }
        }

        var header = new FitsHeader();
        wcs.WriteToHeader(header);
        header.Set("EXPTIME", conditions.ExposureSeconds, "exposure, seconds");
        header.Set("GAIN", conditions.GainElectronsPerAdu, "electrons per ADU");

        // Unsigned 16-bit, the standard astronomical camera representation:
        // BITPIX 16 with BZERO 32768 shifts the signed range to 0..65535.
        var stored = new double[height, width];
        Array.Copy(pixels, stored, pixels.Length);

        return new FitsImage(width, height, FitsBitPix.Int16, bzero: 32768.0, bscale: 1.0, stored, header);
    }

    /// <summary>
    /// Builds the WCS for a frame pointed at a given place, from the physical
    /// optics rather than from an assumed pixel scale, so a rendered field's
    /// size is whatever the focal length and sensor actually imply.
    /// </summary>
    public static TanWcsSolution BuildWcs(
        double centreRaDegrees,
        double centreDecDegrees,
        int width,
        int height,
        double pixelPitchMicrons,
        double focalLengthMillimetres,
        double rotationDegrees = 0.0,
        bool mirrored = false)
    {
        double scaleDegrees = PlateScale.ScaleArcsecondsPerPixel(pixelPitchMicrons, focalLengthMillimetres) / 3600.0;
        double angle = rotationDegrees * Math.PI / 180.0;
        double cos = Math.Cos(angle);
        double sin = Math.Sin(angle);

        // The CD matrix maps pixel offsets to (east, north) degrees, so each of
        // its columns is a pixel axis expressed as a direction on the sky.
        //
        // The +y column is placed at position angle `rotationDegrees` east of
        // north, which is what makes TanWcsSolution.RotationDegrees return that
        // angle back exactly. The +x column then sits 90 degrees from it, and
        // which side decides parity: for an unmirrored frame east falls to the
        // left of north, putting +x at (rotation - 90) degrees and making the
        // determinant negative. A mirror -- a star diagonal, usually -- puts +x
        // on the other side and flips that sign, which is the signal D12's
        // correction arrows key off.
        double handedness = mirrored ? -1.0 : 1.0;

        return new TanWcsSolution(
            Crpix1: (width + 1) / 2.0,
            Crpix2: (height + 1) / 2.0,
            Crval1Degrees: centreRaDegrees,
            Crval2Degrees: centreDecDegrees,
            Cd1_1: -handedness * scaleDegrees * cos,
            Cd1_2: scaleDegrees * sin,
            Cd2_1: handedness * scaleDegrees * sin,
            Cd2_2: scaleDegrees * cos);
    }

    private static double Transmission(ObservingConditions conditions, double x, double y, int width, int height)
    {
        double baseTransmission = Math.Clamp(conditions.CloudTransmission, 0.0, 1.0);
        if (conditions.CloudGradientFraction == 0.0)
        {
            return baseTransmission;
        }

        // Diagonal ramp across the frame, normalised to +/- half the requested
        // fractional variation about the mean.
        double alongDiagonal = ((x / width) + (y / height)) / 2.0;
        double factor = 1.0 + conditions.CloudGradientFraction * (alongDiagonal - 0.5);
        return Math.Clamp(baseTransmission * factor, 0.0, 1.0);
    }

    private static void AddGaussian(double[,] target, int width, int height, double centreX, double centreY, double flux, double sigma)
    {
        int radius = Math.Max(1, (int)Math.Ceiling(4.0 * sigma));
        int x0 = Math.Max(0, (int)Math.Floor(centreX) - radius);
        int x1 = Math.Min(width - 1, (int)Math.Ceiling(centreX) + radius);
        int y0 = Math.Max(0, (int)Math.Floor(centreY) - radius);
        int y1 = Math.Min(height - 1, (int)Math.Ceiling(centreY) + radius);

        double twoSigmaSquared = 2.0 * sigma * sigma;
        double normalisation = flux / (2.0 * Math.PI * sigma * sigma);

        for (int y = y0; y <= y1; y++)
        {
            double dy = y - centreY;
            for (int x = x0; x <= x1; x++)
            {
                double dx = x - centreX;
                target[y, x] += normalisation * Math.Exp(-(dx * dx + dy * dy) / twoSigmaSquared);
            }
        }
    }

    /// <summary>
    /// Poisson deviate: Knuth's product method while the mean is small enough
    /// for it to be cheap, a Gaussian approximation above that, where the two
    /// are indistinguishable anyway.
    /// </summary>
    private static double PoissonSample(Random random, double mean)
    {
        if (mean <= 0)
        {
            return 0.0;
        }

        if (mean > 100.0)
        {
            return mean + Math.Sqrt(mean) * NextGaussian(random);
        }

        double limit = Math.Exp(-mean);
        double product = random.NextDouble();
        int count = 0;
        while (product > limit)
        {
            count++;
            product *= random.NextDouble();
        }

        return count;
    }

    private static double NextGaussian(Random random)
    {
        double u1 = 1.0 - random.NextDouble();
        double u2 = random.NextDouble();
        return Math.Sqrt(-2.0 * Math.Log(u1)) * Math.Cos(2.0 * Math.PI * u2);
    }
}
