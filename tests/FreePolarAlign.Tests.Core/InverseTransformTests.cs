using FreePolarAlign.Core.Astrometry;
using Xunit;

namespace FreePolarAlign.Tests.Core;

/// <summary>
/// <see cref="TopocentricConverter.FromAltAz"/> must be a true inverse of
/// <see cref="TopocentricConverter.ToAltAz"/>, not an approximation of one.
/// Anything commanded through it -- a target chosen by altitude, a simulated
/// mount pointing -- inherits its error directly, and an inverse that was merely
/// close would put a systematic offset into every such position.
/// </summary>
public class InverseTransformTests
{
    private static readonly ObserverSite[] Sites =
    {
        new(45.0, -75.0, 100.0),
        new(60.0, 25.0, 50.0),
        new(0.1, 30.0, 0.0),
        new(-33.9, 151.2, 40.0),
        new(-55.0, -68.3, 10.0),
    };

    private static readonly DateTime[] Epochs =
    {
        new(1996, 6, 15, 0, 0, 0, DateTimeKind.Utc),
        new(2012, 9, 22, 6, 30, 0, DateTimeKind.Utc),
        new(2026, 9, 9, 22, 15, 0, DateTimeKind.Utc),
    };

    public static IEnumerable<object[]> Cases()
    {
        for (int site = 0; site < Sites.Length; site++)
        {
            for (int epoch = 0; epoch < Epochs.Length; epoch++)
            {
                yield return new object[] { site, epoch };
            }
        }
    }

    /// <summary>
    /// Round trip through both directions, in the vacuum case and through a
    /// standard atmosphere. Refraction is the part that is not a rotation, so
    /// it is the part an inverse is most likely to get wrong.
    /// </summary>
    [Theory]
    [MemberData(nameof(Cases))]
    public void FromAltAz_InvertsToAltAz_ToBetterThanAMilliarcsecond(int siteIndex, int epochIndex)
    {
        ObserverSite site = Sites[siteIndex];
        DateTime utc = Epochs[epochIndex];

        foreach (AtmosphericConditions atmosphere in new[]
                 {
                     AtmosphericConditions.Vacuum,
                     new AtmosphericConditions(1013.25, 10.0, 0.4, 0.55),
                 })
        {
            // Altitudes from just above the horizon, where refraction is
            // strongest and hardest to invert, up to the zenith.
            foreach (double altitude in new[] { 5.0, 15.0, 30.0, 50.0, 75.0, 89.0 })
            {
                foreach (double azimuth in new[] { 0.0, 73.0, 180.0, 291.0 })
                {
                    var target = new HorizontalCoordinates(azimuth, altitude);

                    (double ra, double dec) = TopocentricConverter.FromAltAz(target, utc, site, atmosphere);
                    HorizontalCoordinates achieved = TopocentricConverter.ToAltAz(ra, dec, utc, site, atmosphere);

                    double errorArcseconds = SeparationArcseconds(target, achieved);
                    Assert.True(errorArcseconds < 0.001,
                        $"site {site.LatitudeDegrees}, {utc:o}, alt {altitude}, az {azimuth}, " +
                        $"pressure {atmosphere.PressureHPa}: round trip off by {errorArcseconds:F6}\"");
                }
            }
        }
    }

    /// <summary>
    /// The inverse must also round trip the other way -- ICRS in, ICRS out --
    /// which catches an error that happened to be self-consistent in the horizon
    /// frame alone.
    /// </summary>
    [Fact]
    public void ToAltAz_ThenBack_ReturnsTheOriginalIcrsPosition()
    {
        var site = new ObserverSite(45.0, -75.0, 100.0);
        var utc = new DateTime(2026, 9, 9, 22, 15, 0, DateTimeKind.Utc);
        var atmosphere = new AtmosphericConditions(1013.25, 10.0, 0.0, 0.55);

        foreach ((double ra, double dec) in new[] { (10.0, 85.0), (250.0, 60.0), (95.0, 20.0), (340.0, 41.0) })
        {
            HorizontalCoordinates horizontal = TopocentricConverter.ToAltAz(ra, dec, utc, site, atmosphere);

            // Only meaningful above the horizon: refraction is clamped below it,
            // so the forward transform is deliberately not invertible there.
            if (horizontal.AltitudeDegrees < 5.0)
            {
                continue;
            }

            (double raBack, double decBack) = TopocentricConverter.FromAltAz(horizontal, utc, site, atmosphere);

            double errorArcseconds = SeparationArcseconds(
                new HorizontalCoordinates(ra, dec), new HorizontalCoordinates(raBack, decBack));
            Assert.True(errorArcseconds < 0.001, $"RA/Dec {ra},{dec} came back {errorArcseconds:F6}\" away");
        }
    }

    private static double SeparationArcseconds(HorizontalCoordinates a, HorizontalCoordinates b)
    {
        double d2r = Math.PI / 180.0;
        double lat1 = a.AltitudeDegrees * d2r, lat2 = b.AltitudeDegrees * d2r;
        double dLat = lat2 - lat1;
        double dLon = (b.AzimuthDegrees - a.AzimuthDegrees) * d2r;

        double h = Math.Sin(dLat / 2.0) * Math.Sin(dLat / 2.0)
                 + Math.Cos(lat1) * Math.Cos(lat2) * Math.Sin(dLon / 2.0) * Math.Sin(dLon / 2.0);
        return 2.0 * Math.Asin(Math.Sqrt(Math.Clamp(h, 0.0, 1.0))) * 180.0 / Math.PI * 3600.0;
    }
}
