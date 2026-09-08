using System.Globalization;
using System.Text.Json;
using FreePolarAlign.Core.Astrometry;
using Xunit;

namespace FreePolarAlign.Tests.Core;

/// <summary>
/// Phase 0 exit criterion: J2000 ICRS RA/Dec -> topocentric Alt/Az must agree
/// with Astropy to better than 1 arcsecond, for both the no-refraction
/// ("vacuum") and refracted cases, across every row of the committed
/// reference fixture (5 sites, 5 epochs 2000-2030, 6 targets -- 150 rows, no
/// hand-picked subset).
///
/// Agreement is measured as the great-circle separation between the two
/// (azimuth, altitude) points on the sky, rather than comparing azimuth and
/// altitude independently: an azimuth-only comparison is degenerate near the
/// zenith (a large azimuth difference there corresponds to a tiny actual
/// pointing difference), so total angular separation is the physically
/// meaningful -- and stricter -- metric.
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
            AtmosphericConditions.Vacuum);

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
            atmosphere);

        double errorArcsec = AngularSeparationArcseconds(row.AzRefractedDeg, row.AltRefractedDeg, actual.AzimuthDegrees, actual.AltitudeDegrees);

        Assert.True(
            errorArcsec < ToleranceArcseconds,
            Describe(row, index, "refracted", actual, row.AzRefractedDeg, row.AltRefractedDeg, errorArcsec));
    }

    private static string Describe(AltAzFixtureRow row, int index, string kind, HorizontalCoordinates actual, double expectedAz, double expectedAlt, double errorArcsec) =>
        $"row {index} [{row.Site}, {row.Utc}, RA={row.RaIcrsDeg}, Dec={row.DecIcrsDeg}] {kind}: " +
        $"expected Az={expectedAz:F6} Alt={expectedAlt:F6}, got Az={actual.AzimuthDegrees:F6} Alt={actual.AltitudeDegrees:F6}, " +
        $"separation={errorArcsec:F4}\" (budget {ToleranceArcseconds}\")";

    private static DateTime ParseUtc(string utc) =>
        DateTime.Parse(utc, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal);

    private static double AngularSeparationArcseconds(double az1Deg, double alt1Deg, double az2Deg, double alt2Deg)
    {
        double d2r = Math.PI / 180.0;
        double az1 = az1Deg * d2r, alt1 = alt1Deg * d2r;
        double az2 = az2Deg * d2r, alt2 = alt2Deg * d2r;

        double x1 = Math.Cos(alt1) * Math.Cos(az1), y1 = Math.Cos(alt1) * Math.Sin(az1), z1 = Math.Sin(alt1);
        double x2 = Math.Cos(alt2) * Math.Cos(az2), y2 = Math.Cos(alt2) * Math.Sin(az2), z2 = Math.Sin(alt2);

        double dot = Math.Clamp(x1 * x2 + y1 * y2 + z1 * z2, -1.0, 1.0);
        double separationRad = Math.Acos(dot);
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
