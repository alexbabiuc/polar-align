using FreePolarAlign.Imaging.Fits;

namespace FreePolarAlign.Imaging.Wcs;

/// <summary>
/// A gnomonic (TAN) WCS solution in CD-matrix form (D12 depends on reading
/// the CD matrix directly, since its determinant encodes handedness/parity).
/// </summary>
public sealed record TanWcsSolution(
    double Crpix1,
    double Crpix2,
    double Crval1Degrees,
    double Crval2Degrees,
    double Cd1_1,
    double Cd1_2,
    double Cd2_1,
    double Cd2_2)
{
    private const double DegToRad = Math.PI / 180.0;
    private const double RadToDeg = 180.0 / Math.PI;

    /// <summary>
    /// The CD matrix's determinant. Its sign encodes image parity/handedness
    /// (D12): a solver-independent way to derive correction direction that
    /// automatically handles star diagonals, mirror flips and arbitrary
    /// camera rotation.
    /// </summary>
    public double Determinant => Cd1_1 * Cd2_2 - Cd1_2 * Cd2_1;

    public static TanWcsSolution FromHeader(FitsHeader header)
    {
        string ctype1 = header.GetString("CTYPE1", "RA---TAN");
        string ctype2 = header.GetString("CTYPE2", "DEC--TAN");
        if (!ctype1.EndsWith("TAN", StringComparison.Ordinal) || !ctype2.EndsWith("TAN", StringComparison.Ordinal))
        {
            throw new NotSupportedException($"Only TAN projection is supported (found CTYPE1='{ctype1}', CTYPE2='{ctype2}').");
        }

        // CD-matrix form is required; CDELT+CROTA2 is not read by this Phase 0 subset (D12 needs the CD matrix directly).
        return new TanWcsSolution(
            header.GetDouble("CRPIX1"),
            header.GetDouble("CRPIX2"),
            header.GetDouble("CRVAL1"),
            header.GetDouble("CRVAL2"),
            header.GetDouble("CD1_1"),
            header.GetDouble("CD1_2"),
            header.GetDouble("CD2_1"),
            header.GetDouble("CD2_2"));
    }

    public void WriteToHeader(FitsHeader header)
    {
        header.Set("CTYPE1", "RA---TAN", "TAN (gnomonic) projection");
        header.Set("CTYPE2", "DEC--TAN", "TAN (gnomonic) projection");
        header.Set("CRPIX1", Crpix1, "reference pixel, axis 1");
        header.Set("CRPIX2", Crpix2, "reference pixel, axis 2");
        header.Set("CRVAL1", Crval1Degrees, "reference RA, degrees");
        header.Set("CRVAL2", Crval2Degrees, "reference Dec, degrees");
        header.Set("CUNIT1", "deg", "axis 1 units");
        header.Set("CUNIT2", "deg", "axis 2 units");
        header.Set("CD1_1", Cd1_1, "WCS CD matrix");
        header.Set("CD1_2", Cd1_2, "WCS CD matrix");
        header.Set("CD2_1", Cd2_1, "WCS CD matrix");
        header.Set("CD2_2", Cd2_2, "WCS CD matrix");
    }

    /// <summary>Pixel (1-indexed FITS convention, as CRPIX is) to (RA, Dec) degrees.</summary>
    public (double RaDegrees, double DecDegrees) PixelToWorld(double x, double y)
    {
        double dx = x - Crpix1;
        double dy = y - Crpix2;

        double xiDeg = Cd1_1 * dx + Cd1_2 * dy;
        double etaDeg = Cd2_1 * dx + Cd2_2 * dy;

        double xi = xiDeg * DegToRad;
        double eta = etaDeg * DegToRad;

        double ra0 = Crval1Degrees * DegToRad;
        double dec0 = Crval2Degrees * DegToRad;

        var (p0, eastAxis, northAxis) = TangentFrame(ra0, dec0);

        double vx = p0.X + xi * eastAxis.X + eta * northAxis.X;
        double vy = p0.Y + xi * eastAxis.Y + eta * northAxis.Y;
        double vz = p0.Z + xi * eastAxis.Z + eta * northAxis.Z;

        double norm = Math.Sqrt(vx * vx + vy * vy + vz * vz);
        vx /= norm; vy /= norm; vz /= norm;

        double dec = Math.Asin(Math.Clamp(vz, -1.0, 1.0));
        double ra = Math.Atan2(vy, vx);
        if (ra < 0)
        {
            ra += 2.0 * Math.PI;
        }

        return (ra * RadToDeg, dec * RadToDeg);
    }

    /// <summary>(RA, Dec) degrees to pixel (1-indexed FITS convention).</summary>
    public (double X, double Y) WorldToPixel(double raDegrees, double decDegrees)
    {
        double ra = raDegrees * DegToRad;
        double dec = decDegrees * DegToRad;
        double ra0 = Crval1Degrees * DegToRad;
        double dec0 = Crval2Degrees * DegToRad;

        var (p0, eastAxis, northAxis) = TangentFrame(ra0, dec0);

        double px = Math.Cos(dec) * Math.Cos(ra);
        double py = Math.Cos(dec) * Math.Sin(ra);
        double pz = Math.Sin(dec);

        double d = px * p0.X + py * p0.Y + pz * p0.Z;
        if (d <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(raDegrees), "Point is more than 90 degrees from the tangent point; TAN projection is undefined there.");
        }

        double xi = (px * eastAxis.X + py * eastAxis.Y + pz * eastAxis.Z) / d;
        double eta = (px * northAxis.X + py * northAxis.Y + pz * northAxis.Z) / d;

        double xiDeg = xi * RadToDeg;
        double etaDeg = eta * RadToDeg;

        double det = Determinant;
        if (det == 0)
        {
            throw new InvalidOperationException("CD matrix is singular.");
        }

        double dx = (Cd2_2 * xiDeg - Cd1_2 * etaDeg) / det;
        double dy = (Cd1_1 * etaDeg - Cd2_1 * xiDeg) / det;

        return (Crpix1 + dx, Crpix2 + dy);
    }

    private static ((double X, double Y, double Z) P0, (double X, double Y, double Z) East, (double X, double Y, double Z) North) TangentFrame(double raRad, double decRad)
    {
        double cosDec = Math.Cos(decRad), sinDec = Math.Sin(decRad);
        double cosRa = Math.Cos(raRad), sinRa = Math.Sin(raRad);

        var p0 = (cosDec * cosRa, cosDec * sinRa, sinDec);
        var east = (-sinRa, cosRa, 0.0);
        var north = (-sinDec * cosRa, -sinDec * sinRa, cosDec);
        return (p0, east, north);
    }
}
