using System.Globalization;

namespace FreePolarAlign.Devices.Simulated.SyntheticSky;

/// <summary>One catalogue star: ICRS position and visual magnitude.</summary>
public sealed record CatalogStar(double RaDegrees, double DecDegrees, double Magnitude);

/// <summary>
/// A star catalogue the renderer draws from.
///
/// Deliberately a real catalogue rather than random points. The virtual
/// observatory exists to verify accuracy claims end to end, which includes the
/// plate solve, and a solve can only succeed against genuine sky: the star
/// pattern has to be the one the solver's index was built from. Synthetic star
/// fields would test the renderer and the fit while quietly skipping the solver,
/// which is the part most likely to be wrong.
/// </summary>
public sealed class StarCatalog
{
    private readonly CatalogStar[] _stars;

    // Unit vectors precomputed alongside the stars: a cone query is a dot
    // product per star, and rebuilding these per query dominated the render.
    private readonly double[] _x;
    private readonly double[] _y;
    private readonly double[] _z;

    public StarCatalog(IEnumerable<CatalogStar> stars)
    {
        ArgumentNullException.ThrowIfNull(stars);
        _stars = stars.ToArray();

        _x = new double[_stars.Length];
        _y = new double[_stars.Length];
        _z = new double[_stars.Length];

        for (int i = 0; i < _stars.Length; i++)
        {
            double ra = _stars[i].RaDegrees * Math.PI / 180.0;
            double dec = _stars[i].DecDegrees * Math.PI / 180.0;
            double cosDec = Math.Cos(dec);
            _x[i] = cosDec * Math.Cos(ra);
            _y[i] = cosDec * Math.Sin(ra);
            _z[i] = Math.Sin(dec);
        }
    }

    public int Count => _stars.Length;

    /// <summary>
    /// Loads the committed Tycho-2 subset format: a comment/header block, then
    /// <c>ra_deg,dec_deg,vtmag</c> rows.
    /// </summary>
    public static StarCatalog LoadCsv(string path)
    {
        var stars = new List<CatalogStar>();

        foreach (string line in File.ReadLines(path))
        {
            if (line.Length == 0 || line[0] == '#' || line.StartsWith("ra_deg", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            string[] fields = line.Split(',');
            if (fields.Length < 3)
            {
                continue;
            }

            if (double.TryParse(fields[0], NumberStyles.Float, CultureInfo.InvariantCulture, out double ra)
                && double.TryParse(fields[1], NumberStyles.Float, CultureInfo.InvariantCulture, out double dec)
                && double.TryParse(fields[2], NumberStyles.Float, CultureInfo.InvariantCulture, out double magnitude))
            {
                stars.Add(new CatalogStar(ra, dec, magnitude));
            }
        }

        if (stars.Count == 0)
        {
            throw new InvalidDataException($"No catalogue stars parsed from '{path}'.");
        }

        return new StarCatalog(stars);
    }

    /// <summary>Stars within <paramref name="radiusDegrees"/> of a point, brighter than a magnitude limit.</summary>
    public IReadOnlyList<CatalogStar> Cone(double raDegrees, double decDegrees, double radiusDegrees, double magnitudeLimit = double.PositiveInfinity)
    {
        double ra = raDegrees * Math.PI / 180.0;
        double dec = decDegrees * Math.PI / 180.0;
        double cosDec = Math.Cos(dec);
        double cx = cosDec * Math.Cos(ra);
        double cy = cosDec * Math.Sin(ra);
        double cz = Math.Sin(dec);

        double minimumDot = Math.Cos(radiusDegrees * Math.PI / 180.0);

        var result = new List<CatalogStar>();
        for (int i = 0; i < _stars.Length; i++)
        {
            if (_stars[i].Magnitude > magnitudeLimit)
            {
                continue;
            }

            if (_x[i] * cx + _y[i] * cy + _z[i] * cz >= minimumDot)
            {
                result.Add(_stars[i]);
            }
        }

        return result;
    }
}
