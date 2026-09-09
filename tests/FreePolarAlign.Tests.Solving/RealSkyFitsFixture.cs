using System.Globalization;
using System.Net.Http;
using FreePolarAlign.Imaging.Fits;
using FreePolarAlign.Imaging.Wcs;

namespace FreePolarAlign.Tests.Solving;

/// <summary>
/// Builds a FITS image of a real star field for the one test that needs to
/// prove Watney can actually solve something (as opposed to the pure
/// translation-logic tests elsewhere in this project).
/// </summary>
/// <remarks>
/// There is no synthetic sky renderer available yet (it is being built
/// separately, and this project is explicitly not the place for one), so this
/// takes the other option D3/the roadmap leaves open: real catalogue stars,
/// fetched from VizieR's Tycho-2 cone search, painted onto a blank frame at a
/// WCS this test chooses -- so the "known truth" to check Watney's answer
/// against is exact, not measured. Only <see cref="FreePolarAlign.Imaging"/>
/// (FITS writer, <see cref="TanWcsSolution"/>) is used to build the file;
/// the star-painting here is a one-off test fixture, not a reusable renderer.
/// </remarks>
internal static class RealSkyFitsFixture
{
    private static readonly HttpClient HttpClient = new() { Timeout = TimeSpan.FromSeconds(30) };

    public sealed record CatalogStar(double RaDegrees, double DecDegrees, double MagnitudeVt);

    /// <summary>
    /// Cone-searches VizieR's Tycho-2 main catalog (I/239/tyc_main) for real
    /// stars around a field center. Plain HTTPS, no authentication, same
    /// pattern as D13's index-pack downloads.
    /// </summary>
    public static async Task<IReadOnlyList<CatalogStar>> FetchTycho2StarsAsync(double centerRaDegrees, double centerDecDegrees, double radiusDegrees)
    {
        string url = "https://vizier.cds.unistra.fr/viz-bin/asu-tsv" +
                     "?-source=I/239/tyc_main" +
                     $"&-c={centerRaDegrees.ToString(CultureInfo.InvariantCulture)}+{centerDecDegrees.ToString(CultureInfo.InvariantCulture)}" +
                     $"&-c.rd={radiusDegrees.ToString(CultureInfo.InvariantCulture)}" +
                     "&-out=_RAJ2000,_DEJ2000,VTmag" +
                     "&-out.max=99999" +
                     "&-sort=VTmag";

        string tsv = await HttpClient.GetStringAsync(url).ConfigureAwait(false);

        var stars = new List<CatalogStar>();
        foreach (string rawLine in tsv.Split('\n'))
        {
            string line = rawLine.TrimEnd('\r');
            if (line.Length == 0 || line.StartsWith('#') || line.StartsWith('-') || line.StartsWith("_RAJ2000"))
            {
                continue;
            }

            string[] fields = line.Split('\t');
            if (fields.Length < 3)
            {
                continue;
            }

            if (double.TryParse(fields[0], NumberStyles.Float, CultureInfo.InvariantCulture, out double ra) &&
                double.TryParse(fields[1], NumberStyles.Float, CultureInfo.InvariantCulture, out double dec) &&
                double.TryParse(fields[2], NumberStyles.Float, CultureInfo.InvariantCulture, out double mag))
            {
                stars.Add(new CatalogStar(ra, dec, mag));
            }
        }

        return stars;
    }

    /// <summary>
    /// Paints real catalogue stars onto a blank frame at a chosen TAN WCS,
    /// using only <see cref="TanWcsSolution.WorldToPixel"/> for placement.
    /// </summary>
    public static FitsImage RenderStarField(
        IReadOnlyList<CatalogStar> stars, int width, int height, TanWcsSolution wcs, int seed = 12345)
    {
        var pixels = new double[height, width];
        var rng = new Random(seed);

        const double background = 500.0;
        const double noiseSigma = 8.0;
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                pixels[y, x] = background + GaussianNoise(rng) * noiseSigma;
            }
        }

        foreach (CatalogStar star in stars)
        {
            (double px, double py) pixel;
            try
            {
                pixel = wcs.WorldToPixel(star.RaDegrees, star.DecDegrees);
            }
            catch (ArgumentOutOfRangeException)
            {
                continue;
            }

            double margin = 12.0;
            if (pixel.px < -margin || pixel.px > width + margin || pixel.py < -margin || pixel.py > height + margin)
            {
                continue;
            }

            // Brighter (numerically smaller) VT magnitude -> a taller Gaussian
            // PSF. Values are arbitrary ADU counts; what matters is that
            // brighter real stars stay brighter than fainter real stars.
            double peak = Math.Clamp(28000.0 * Math.Pow(10.0, -0.4 * (star.MagnitudeVt - 2.0)), 200.0, 28000.0);
            const double psfSigma = 1.6;
            int radius = 6;

            int cx = (int)Math.Round(pixel.px - 1.0);
            int cy = (int)Math.Round(pixel.py - 1.0);

            for (int dy = -radius; dy <= radius; dy++)
            {
                int py = cy + dy;
                if (py < 0 || py >= height)
                {
                    continue;
                }

                for (int dx = -radius; dx <= radius; dx++)
                {
                    int px = cx + dx;
                    if (px < 0 || px >= width)
                    {
                        continue;
                    }

                    double fx = px - (pixel.px - 1.0);
                    double fy = py - (pixel.py - 1.0);
                    double value = peak * Math.Exp(-(fx * fx + fy * fy) / (2 * psfSigma * psfSigma));
                    // Watney's DefaultStarDetector has a truncation bug in its
                    // 32-bit histogram path (BitsPerPixel == 32 in
                    // AddScanlineToHistogram casts the shifted value to
                    // ushort), so this stays inside Int16 range rather than
                    // exercising that path.
                    pixels[py, px] = Math.Min(32000.0, pixels[py, px] + value);
                }
            }
        }

        var header = new FitsHeader();
        wcs.WriteToHeader(header);

        return new FitsImage(width, height, FitsBitPix.Int16, bzero: 0.0, bscale: 1.0, pixels, header);
    }

    private static double GaussianNoise(Random rng)
    {
        double u1 = 1.0 - rng.NextDouble();
        double u2 = rng.NextDouble();
        return Math.Sqrt(-2.0 * Math.Log(u1)) * Math.Cos(2.0 * Math.PI * u2);
    }
}
