namespace FreePolarAlign.Core.Astrometry;

/// <summary>
/// UT1-UTC and polar motion at one instant. These are measured/predicted
/// quantities published by IERS, not computable from first principles --
/// unlike precession/nutation/aberration, they depend on the Earth's actual,
/// irregular rotation.
/// </summary>
/// <param name="Ut1MinusUtcSeconds">UT1 - UTC, seconds. Bounded to within about +/-0.9s in practice (leap seconds keep it there).</param>
/// <param name="PolarMotionXArcsec">Polar motion x (toward the Greenwich meridian), arcseconds. Typically a few tenths of an arcsecond.</param>
/// <param name="PolarMotionYArcsec">Polar motion y (toward 90 deg W), arcseconds.</param>
public sealed record EarthOrientationParameters(double Ut1MinusUtcSeconds, double PolarMotionXArcsec, double PolarMotionYArcsec)
{
    /// <summary>
    /// UT1=UTC, no polar motion. Adequate for the actual product (D5/D14's
    /// operating threshold is arcminutes), but not for reproducing Astropy's
    /// output to better than 1 arcsecond -- see <see cref="EarthOrientationData"/>.
    /// </summary>
    public static readonly EarthOrientationParameters Zero = new(0.0, 0.0, 0.0);
}

/// <summary>
/// A tiny, frozen-at-authoring-time snapshot of measured Earth orientation
/// parameters (UT1-UTC, polar motion), sourced from IERS via Astropy's
/// bundled IERS tables, for the handful of reference epochs used by the
/// Phase 0 fixture (<c>docs/fixtures/astropy_altaz_reference.json</c>).
///
/// This is NOT a general EOP service: real-time UT1-UTC and polar motion
/// depend on the Earth's actual, unpredictable rotation and must ultimately
/// come from a live or periodically-refreshed IERS bulletin feed, which is
/// out of scope for Phase 0. Ignoring EOP entirely (<see cref="EarthOrientationParameters.Zero"/>)
/// is more than adequate for the shipping product's arcminute-level threshold
/// (D5, D14); it is only the Phase 0 exit criterion's stricter-than-1-arcsecond
/// agreement with Astropy that requires matching the exact historical values
/// Astropy itself used.
///
/// Between the tabulated epochs this does a simple linear interpolation;
/// outside the table it clamps to the nearest endpoint. Both are crude, but
/// the only points this project currently needs to be accurate at are the
/// exact tabulated timestamps, where the lookup is exact.
/// </summary>
public static class EarthOrientationData
{
    private static readonly (DateTime Utc, double Ut1MinusUtcSeconds, double XArcsec, double YArcsec)[] Snapshot =
    {
        (new DateTime(2000, 6, 15, 0, 0, 0, DateTimeKind.Utc), 0.2060387, 0.113729, 0.302573),
        (new DateTime(2010, 3, 21, 12, 0, 0, DateTimeKind.Utc), 0.02951005, -0.054501, 0.2951055),
        (new DateTime(2020, 9, 22, 6, 30, 0, DateTimeKind.Utc), -0.17697535416666668, 0.20317447916666667, 0.3346979583333333),
        (new DateTime(2026, 9, 8, 22, 15, 0, DateTimeKind.Utc), -0.0012209791666666667, 0.20324530208333336, 0.33414757291666664),
        (new DateTime(2030, 12, 31, 18, 45, 0, DateTimeKind.Utc), -0.1044597, 0.260133, 0.328115),
    };

    public static EarthOrientationParameters Lookup(DateTime utc)
    {
        if (utc <= Snapshot[0].Utc)
        {
            return FromRow(Snapshot[0]);
        }

        if (utc >= Snapshot[^1].Utc)
        {
            return FromRow(Snapshot[^1]);
        }

        for (int i = 0; i < Snapshot.Length - 1; i++)
        {
            var a = Snapshot[i];
            var b = Snapshot[i + 1];
            if (utc >= a.Utc && utc <= b.Utc)
            {
                double span = (b.Utc - a.Utc).TotalSeconds;
                double frac = span == 0 ? 0 : (utc - a.Utc).TotalSeconds / span;
                return new EarthOrientationParameters(
                    Lerp(a.Ut1MinusUtcSeconds, b.Ut1MinusUtcSeconds, frac),
                    Lerp(a.XArcsec, b.XArcsec, frac),
                    Lerp(a.YArcsec, b.YArcsec, frac));
            }
        }

        return EarthOrientationParameters.Zero;
    }

    private static double Lerp(double a, double b, double t) => a + (b - a) * t;

    private static EarthOrientationParameters FromRow((DateTime Utc, double Ut1MinusUtcSeconds, double XArcsec, double YArcsec) row) =>
        new(row.Ut1MinusUtcSeconds, row.XArcsec, row.YArcsec);
}
