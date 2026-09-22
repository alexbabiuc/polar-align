using System.Globalization;
using FreePolarAlign.Core.Astrometry;
using Xunit;

namespace FreePolarAlign.Tests.Core;

/// <summary>
/// Covers <see cref="ApparentPlace"/>, the J2000 ICRS to apparent-place-of-date
/// conversion a JNow-configured ASCOM driver needs at the device boundary.
///
/// Reference values come from Astropy's <c>TETE</c> frame (true equator, true
/// equinox -- its name for geocentric apparent place), generated with the same
/// astropy version used for the Phase 0 alt/az fixture. They are inline rather
/// than in a fixture file because six rows is enough here: this transform is
/// the first two steps of the chain
/// <see cref="AstrometryFixtureTests"/> already exercises across 300 rows, so
/// what needs establishing is that the chain was cut in the right place and
/// nothing was dropped or double-applied, not the accuracy of precession and
/// nutation themselves.
/// </summary>
public class ApparentPlaceTests
{
    /// <summary>
    /// Tighter than the 1 arcsecond Phase 0 exit criterion but not arbitrarily
    /// so: the residual against Astropy here is the same one D5 measured for the
    /// full chain, about 0.14 arcsec worst case, from the truncated IAU 2000B
    /// nutation series, the first-order aberration formula, and the omitted
    /// solar light deflection. Sitting just above that makes this a regression
    /// guard rather than a restatement of the looser criterion.
    /// </summary>
    private const double ToleranceArcseconds = 0.25;

    [Theory]
    [InlineData(0.0, 0.0, "2026-09-22T03:00:00", 0.349879831, 0.152018938)]
    [InlineData(37.95456067, 89.26410897, "2026-01-01T00:00:00", 46.678379742, 89.378238277)]
    [InlineData(279.23473479, 38.78368896, "2027-06-15T21:30:00", 279.473968615, 38.806075743)]
    [InlineData(101.28715533, -16.71611586, "2030-12-31T12:00:00", 101.643192982, -16.752225195)]
    [InlineData(213.9153, 19.1824, "2015-03-10T06:45:00", 214.097512787, 19.109932927)]
    [InlineData(350.0, -70.0, "2026-09-22T03:00:00", 350.435477300, -69.852856610)]
    public void FromJ2000_MatchesAstropyApparentPlace(
        double raJ2000, double decJ2000, string utc, double expectedRa, double expectedDec)
    {
        var (ra, dec) = ApparentPlace.FromJ2000(raJ2000, decJ2000, ParseUtc(utc));

        double errorArcsec = AngularSeparationArcseconds(expectedRa, expectedDec, ra, dec);

        Assert.True(
            errorArcsec < ToleranceArcseconds,
            $"({raJ2000}, {decJ2000}) at {utc}: expected ({expectedRa}, {expectedDec}), " +
            $"got ({ra}, {dec}) -- {errorArcsec:F4} arcsec apart.");
    }

    [Theory]
    [InlineData(0.0, 0.0, "2026-09-22T03:00:00", 0.349879831, 0.152018938)]
    [InlineData(37.95456067, 89.26410897, "2026-01-01T00:00:00", 46.678379742, 89.378238277)]
    [InlineData(279.23473479, 38.78368896, "2027-06-15T21:30:00", 279.473968615, 38.806075743)]
    [InlineData(101.28715533, -16.71611586, "2030-12-31T12:00:00", 101.643192982, -16.752225195)]
    [InlineData(213.9153, 19.1824, "2015-03-10T06:45:00", 214.097512787, 19.109932927)]
    [InlineData(350.0, -70.0, "2026-09-22T03:00:00", 350.435477300, -69.852856610)]
    public void ToJ2000_MatchesAstropy_InTheOtherDirection(
        double expectedRa, double expectedDec, string utc, double raApparent, double decApparent)
    {
        var (ra, dec) = ApparentPlace.ToJ2000(raApparent, decApparent, ParseUtc(utc));

        double errorArcsec = AngularSeparationArcseconds(expectedRa, expectedDec, ra, dec);

        Assert.True(
            errorArcsec < ToleranceArcseconds,
            $"({raApparent}, {decApparent}) at {utc}: expected ({expectedRa}, {expectedDec}), " +
            $"got ({ra}, {dec}) -- {errorArcsec:F4} arcsec apart.");
    }

    /// <summary>
    /// The device layer applies the two directions back to back -- out on a slew,
    /// back on the next position read -- so anything the pair does not undo
    /// exactly would show up as a systematic pointing offset rather than as
    /// noise. A milliarcsecond is four orders of magnitude inside the budget and
    /// is really just asserting that the aberration inversion converged.
    /// </summary>
    [Theory]
    [InlineData(0.0, 0.0)]
    [InlineData(37.95, 89.26)]
    [InlineData(180.0, -45.0)]
    [InlineData(359.99, 12.3)]
    [InlineData(275.0, -89.9)]
    public void RoundTrip_ReturnsTheOriginalPosition(double raJ2000, double decJ2000)
    {
        var utc = new DateTime(2026, 9, 22, 3, 0, 0, DateTimeKind.Utc);

        var (raApparent, decApparent) = ApparentPlace.FromJ2000(raJ2000, decJ2000, utc);
        var (ra, dec) = ApparentPlace.ToJ2000(raApparent, decApparent, utc);

        Assert.True(
            AngularSeparationArcseconds(raJ2000, decJ2000, ra, dec) < 0.001,
            $"Round trip of ({raJ2000}, {decJ2000}) gave ({ra}, {dec}).");
    }

    /// <summary>
    /// The reason any of this exists (D16): handing J2000 coordinates to a JNow
    /// driver unconverted misplaces the target by the accumulated precession,
    /// which at the current epoch is around 22 arcminutes of general precession
    /// -- hundreds of times the accuracy the reported figure needs. How much of
    /// that lands as on-sky displacement depends on where the star sits relative
    /// to the ecliptic, so this asserts only that the shift is tens of
    /// arcminutes; a conversion that quietly became a no-op would still pass the
    /// round-trip test above, which is why the magnitude is checked at all.
    /// </summary>
    [Fact]
    public void FromJ2000_ShiftsByArcminutesAtTheCurrentEpoch()
    {
        var utc = new DateTime(2026, 9, 22, 3, 0, 0, DateTimeKind.Utc);
        const double raJ2000 = 279.23473479;
        const double decJ2000 = 38.78368896;

        var (ra, dec) = ApparentPlace.FromJ2000(raJ2000, decJ2000, utc);
        double shiftArcminutes = AngularSeparationArcseconds(raJ2000, decJ2000, ra, dec) / 60.0;

        Assert.InRange(shiftArcminutes, 5.0, 30.0);
    }

    private static DateTime ParseUtc(string value) =>
        DateTime.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal);

    private static double AngularSeparationArcseconds(double ra1, double dec1, double ra2, double dec2)
    {
        const double toRadians = Math.PI / 180.0;
        double d1 = dec1 * toRadians, d2 = dec2 * toRadians;
        double dRa = (ra2 - ra1) * toRadians;

        double cosSeparation = Math.Sin(d1) * Math.Sin(d2) + Math.Cos(d1) * Math.Cos(d2) * Math.Cos(dRa);
        cosSeparation = Math.Clamp(cosSeparation, -1.0, 1.0);

        return Math.Acos(cosSeparation) / toRadians * 3600.0;
    }
}
