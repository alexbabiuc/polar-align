namespace FreePolarAlign.Core.Astrometry;

internal static class AstrometryConstants
{
    public const double DegreesToRadians = Math.PI / 180.0;
    public const double RadiansToDegrees = 180.0 / Math.PI;

    /// <summary>Arcseconds to radians.</summary>
    public const double ArcsecToRadians = DegreesToRadians / 3600.0;

    /// <summary>Milliarcseconds to radians.</summary>
    public const double MilliarcsecToRadians = ArcsecToRadians / 1000.0;

    /// <summary>Arcseconds per full turn (360 degrees).</summary>
    public const double ArcsecPerTurn = 1296000.0;

    public const double TwoPi = 2.0 * Math.PI;

    /// <summary>Julian date of the J2000.0 epoch.</summary>
    public const double JulianDateJ2000 = 2451545.0;

    /// <summary>Days per Julian century.</summary>
    public const double DaysPerJulianCentury = 36525.0;

    /// <summary>
    /// Speed of light in AU/day (IAU (2012) exact value: 299792458 m/s and
    /// 1 au = 149597870700 m give this to full double precision).
    /// </summary>
    public const double SpeedOfLightAuPerDay = 173.14463267424;
}
