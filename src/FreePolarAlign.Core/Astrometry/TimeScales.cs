namespace FreePolarAlign.Core.Astrometry;

/// <summary>
/// UTC/TT/UT1 conversions and Julian date arithmetic. TT only needs to be
/// known to within a few seconds for this project's purposes (it drives
/// precession/nutation polynomials whose rates are arcseconds-per-century, so
/// even a whole minute of TT error is many orders of magnitude below the 1
/// arcsecond budget); the leap-second table below is exact regardless.
/// </summary>
internal static class TimeScales
{
    /// <summary>TT - TAI, a fixed offset by definition.</summary>
    private const double TtMinusTaiSeconds = 32.184;

    /// <summary>
    /// TAI - UTC (leap seconds) at each effective date, i.e. the standard
    /// public leap-second table maintained by IERS Bulletin C. Whole seconds,
    /// unambiguous, identical in every correct implementation.
    /// </summary>
    private static readonly (DateTime EffectiveUtc, double TaiMinusUtc)[] LeapSeconds =
    {
        (new DateTime(1972, 1, 1), 10.0),
        (new DateTime(1972, 7, 1), 11.0),
        (new DateTime(1973, 1, 1), 12.0),
        (new DateTime(1974, 1, 1), 13.0),
        (new DateTime(1975, 1, 1), 14.0),
        (new DateTime(1976, 1, 1), 15.0),
        (new DateTime(1977, 1, 1), 16.0),
        (new DateTime(1978, 1, 1), 17.0),
        (new DateTime(1979, 1, 1), 18.0),
        (new DateTime(1980, 1, 1), 19.0),
        (new DateTime(1981, 7, 1), 20.0),
        (new DateTime(1982, 7, 1), 21.0),
        (new DateTime(1983, 7, 1), 22.0),
        (new DateTime(1985, 7, 1), 23.0),
        (new DateTime(1988, 1, 1), 24.0),
        (new DateTime(1990, 1, 1), 25.0),
        (new DateTime(1991, 1, 1), 26.0),
        (new DateTime(1992, 7, 1), 27.0),
        (new DateTime(1993, 7, 1), 28.0),
        (new DateTime(1994, 7, 1), 29.0),
        (new DateTime(1996, 1, 1), 30.0),
        (new DateTime(1997, 7, 1), 31.0),
        (new DateTime(1999, 1, 1), 32.0),
        (new DateTime(2006, 1, 1), 33.0),
        (new DateTime(2009, 1, 1), 34.0),
        (new DateTime(2012, 7, 1), 35.0),
        (new DateTime(2015, 7, 1), 36.0),
        (new DateTime(2017, 1, 1), 37.0),
    };

    /// <summary>Julian date (UTC scale, days) from a <see cref="DateTime"/> assumed to be UTC.</summary>
    public static double ToJulianDate(DateTime utc)
    {
        // DateTime.ToOADate() is exact for the Gregorian calendar range this
        // project cares about; converting via the well-known OA epoch offset
        // avoids re-deriving the Gregorian calendar arithmetic here.
        return utc.ToOADate() + 2415018.5;
    }

    private static double TaiMinusUtcSeconds(DateTime utc)
    {
        double result = LeapSeconds[0].TaiMinusUtc;
        foreach (var (effective, value) in LeapSeconds)
        {
            if (utc >= effective)
            {
                result = value;
            }
        }

        return result;
    }

    /// <summary>Terrestrial Time Julian date from a UTC <see cref="DateTime"/>.</summary>
    public static double ToTerrestrialTimeJulianDate(DateTime utc)
    {
        double jdUtc = ToJulianDate(utc);
        double ttMinusUtcSeconds = TaiMinusUtcSeconds(utc) + TtMinusTaiSeconds;
        return jdUtc + ttMinusUtcSeconds / 86400.0;
    }

    /// <summary>UT1 Julian date from a UTC <see cref="DateTime"/> and a UT1-UTC offset in seconds.</summary>
    public static double ToUt1JulianDate(DateTime utc, double ut1MinusUtcSeconds)
    {
        return ToJulianDate(utc) + ut1MinusUtcSeconds / 86400.0;
    }

    /// <summary>Julian centuries of TT elapsed since J2000.0.</summary>
    public static double JulianCenturiesTt(double ttJulianDate)
    {
        return (ttJulianDate - AstrometryConstants.JulianDateJ2000) / AstrometryConstants.DaysPerJulianCentury;
    }
}
