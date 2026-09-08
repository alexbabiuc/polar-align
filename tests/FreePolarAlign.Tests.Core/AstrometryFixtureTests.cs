using System.Globalization;
using System.Text.Json;
using FreePolarAlign.Core.Astrometry;
using Xunit;

namespace FreePolarAlign.Tests.Core;

/// <summary>
/// Phase 0 exit criterion: J2000 ICRS RA/Dec -> topocentric Alt/Az must agree
/// with Astropy to better than 1 arcsecond, for both the no-refraction
/// ("vacuum") and refracted cases, across every row of the committed reference
/// fixture (150 deliberate grid rows spanning 5 sites in both hemispheres, 5
/// epochs and 6 declinations, plus 150 randomized rows with varied
/// atmospheres -- no hand-picked subset).
///
/// Each row supplies the measured IERS Earth orientation (UT1-UTC and polar
/// motion) that Astropy itself used, as an *input*. That is deliberate: Earth
/// orientation is measured data, not a computable quantity, so feeding it in
/// is what makes this a test of the transform rather than a test of whatever
/// Earth-orientation guess the library happens to hold. See
/// <see cref="ZeroEarthOrientation_DegradesWithinItsDocumentedBound"/> for
/// what the no-EOP default costs.
///
/// Agreement is measured as the great-circle separation between the two
/// (azimuth, altitude) points, rather than comparing azimuth and altitude
/// independently: an azimuth-only comparison is degenerate near the zenith,
/// where a large azimuth difference is a tiny pointing difference.
/// </summary>
public class AstrometryFixtureTests
{
    private const double ToleranceArcseconds = 1.0;

    private static readonly IReadOnlyList<AltAzFixtureRow> Rows = LoadRows();

    public static IEnumerable<object[]> RowIndices() =>
        Enumerable.Range(0, Rows.Count).Select(i => new object[] { i });

    [Theory]
    [MemberData(nameof(RowIndices))]
    public void VacuumAltAz_MatchesAstropy_WithinOneArcsecond(int index)
    {
        AltAzFixtureRow row = Rows[index];
        HorizontalCoordinates actual = TopocentricConverter.ToAltAz(
            row.RaIcrsDeg,
            row.DecIcrsDeg,
            ParseUtc(row.Utc),
            new ObserverSite(row.LatDeg, row.LonDeg, row.HeightM),
            AtmosphericConditions.Vacuum,
            EarthOrientationOf(row));

        double errorArcsec = AngularSeparationArcseconds(row.AzVacuumDeg, row.AltVacuumDeg, actual.AzimuthDegrees, actual.AltitudeDegrees);

        Assert.True(
            errorArcsec < ToleranceArcseconds,
            Describe(row, index, "vacuum", actual, row.AzVacuumDeg, row.AltVacuumDeg, errorArcsec));
    }

    [Theory]
    [MemberData(nameof(RowIndices))]
    public void RefractedAltAz_MatchesAstropy_WithinOneArcsecond(int index)
    {
        AltAzFixtureRow row = Rows[index];
        var atmosphere = new AtmosphericConditions(row.PressureHpa, row.TemperatureC, row.RelativeHumidity, row.WavelengthUm);
        HorizontalCoordinates actual = TopocentricConverter.ToAltAz(
            row.RaIcrsDeg,
            row.DecIcrsDeg,
            ParseUtc(row.Utc),
            new ObserverSite(row.LatDeg, row.LonDeg, row.HeightM),
            atmosphere,
            EarthOrientationOf(row));

        double errorArcsec = AngularSeparationArcseconds(row.AzRefractedDeg, row.AltRefractedDeg, actual.AzimuthDegrees, actual.AltitudeDegrees);

        Assert.True(
            errorArcsec < ToleranceArcseconds,
            Describe(row, index, "refracted", actual, row.AzRefractedDeg, row.AltRefractedDeg, errorArcsec));
    }

    /// <summary>
    /// Characterizes -- rather than hides -- the cost of the default, no-Earth-
    /// orientation path. UT1-UTC is bounded to roughly +/-0.9 s by the leap
    /// second rules, and the Earth turns 15.041 arcsec per second of UT1, so
    /// ignoring it can misplace a target by around 14 arcsec; polar motion adds
    /// a few tenths. That is immaterial against this project's 10 arcminute
    /// alignment threshold (D5, D14), and it is the honest default because
    /// there is no way to derive the value -- but it must be a measured,
    /// asserted bound rather than an unexamined assumption (D11: the software
    /// must know when it does not know).
    /// </summary>
    [Fact]
    public void ZeroEarthOrientation_DegradesWithinItsDocumentedBound()
    {
        const double boundArcseconds = 20.0;
        double worst = 0.0;

        foreach (AltAzFixtureRow row in Rows)
        {
            HorizontalCoordinates actual = TopocentricConverter.ToAltAz(
                row.RaIcrsDeg,
                row.DecIcrsDeg,
                ParseUtc(row.Utc),
                new ObserverSite(row.LatDeg, row.LonDeg, row.HeightM),
                AtmosphericConditions.Vacuum);

            worst = Math.Max(worst, AngularSeparationArcseconds(
                row.AzVacuumDeg, row.AltVacuumDeg, actual.AzimuthDegrees, actual.AltitudeDegrees));
        }

        Assert.True(worst < boundArcseconds, $"worst no-EOP error {worst:F3}\" exceeded the documented {boundArcseconds}\" bound");
        Assert.True(worst > ToleranceArcseconds, $"no-EOP error {worst:F3}\" is suspiciously small -- is EOP being supplied after all?");
    }

    private static EarthOrientationParameters EarthOrientationOf(AltAzFixtureRow row) =>
        new(row.Ut1MinusUtcSeconds, row.PolarMotionXArcsec, row.PolarMotionYArcsec);

    private static string Describe(AltAzFixtureRow row, int index, string kind, HorizontalCoordinates actual, double expectedAz, double expectedAlt, double errorArcsec) =>
        $"row {index} [{row.Site}, {row.Utc}, RA={row.RaIcrsDeg}, Dec={row.DecIcrsDeg}] {kind}: " +
        $"expected Az={expectedAz:F6} Alt={expectedAlt:F6}, got Az={actual.AzimuthDegrees:F6} Alt={actual.AltitudeDegrees:F6}, " +
        $"separation={errorArcsec:F4}\" (budget {ToleranceArcseconds}\")";

    private static DateTime ParseUtc(string utc) =>
        DateTime.Parse(utc, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal);

    /// <summary>
    /// Great-circle separation, via the haversine form. The arccos-of-dot-product
    /// form has a noise floor around 0.003 arcsec for near-identical directions,
    /// which would sit inside the errors being measured here.
    /// </summary>
    private static double AngularSeparationArcseconds(double az1Deg, double alt1Deg, double az2Deg, double alt2Deg)
    {
        double d2r = Math.PI / 180.0;
        double alt1 = alt1Deg * d2r, alt2 = alt2Deg * d2r;
        double dAlt = alt2 - alt1;
        double dAz = (az2Deg - az1Deg) * d2r;

        double h = Math.Sin(dAlt / 2.0) * Math.Sin(dAlt / 2.0)
                 + Math.Cos(alt1) * Math.Cos(alt2) * Math.Sin(dAz / 2.0) * Math.Sin(dAz / 2.0);
        double separationRad = 2.0 * Math.Asin(Math.Sqrt(Math.Clamp(h, 0.0, 1.0)));
        return separationRad * (180.0 / Math.PI) * 3600.0;
    }

    private static IReadOnlyList<AltAzFixtureRow> LoadRows()
    {
        string path = Path.Combine(AppContext.BaseDirectory, "fixtures", "astropy_altaz_reference.json");
        string json = File.ReadAllText(path);
        AltAzFixtureFile file = JsonSerializer.Deserialize<AltAzFixtureFile>(json)
            ?? throw new InvalidOperationException("Fixture file deserialized to null.");
        if (file.Rows.Count == 0)
        {
            throw new InvalidOperationException("Fixture file contained no rows.");
        }

        return file.Rows;
    }
}
