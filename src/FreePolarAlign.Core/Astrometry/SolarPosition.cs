namespace FreePolarAlign.Core.Astrometry;

/// <summary>
/// Low-precision (Meeus, "Astronomical Algorithms" 2nd ed., ch. 25) geometric
/// solar position, accurate to about 0.01 degree in longitude -- far more than
/// needed here, since it is only used to derive Earth's orbital velocity for
/// annual aberration (~20 arcsec total; see <see cref="Aberration"/>), and a
/// velocity derived from a smooth, analytic position curve is far less
/// sensitive to the position curve's own small errors than the position
/// itself would be.
/// </summary>
internal static class SolarPosition
{
    /// <summary>Earth's heliocentric position, equatorial J2000 frame, in AU.</summary>
    public static Vector3 EarthHeliocentricPositionJ2000(double julianCenturiesTtFromJ2000)
    {
        double t = julianCenturiesTtFromJ2000;

        double l0Deg = Mod360(280.46646 + 36000.76983 * t + 0.0003032 * t * t);
        double mDeg = Mod360(357.52911 + 35999.05029 * t - 0.0001537 * t * t);
        double e = 0.016708634 - 0.000042037 * t - 0.0000001267 * t * t;

        double mRad = mDeg * AstrometryConstants.DegreesToRadians;
        double cDeg = (1.914602 - 0.004817 * t - 0.000014 * t * t) * Math.Sin(mRad)
                    + (0.019993 - 0.000101 * t) * Math.Sin(2 * mRad)
                    + 0.000289 * Math.Sin(3 * mRad);

        double trueLongitudeDeg = l0Deg + cDeg;
        double trueAnomalyDeg = mDeg + cDeg;
        double trueAnomalyRad = trueAnomalyDeg * AstrometryConstants.DegreesToRadians;

        double radiusAu = 1.000001018 * (1 - e * e) / (1 + e * Math.Cos(trueAnomalyRad));

        double lonRad = trueLongitudeDeg * AstrometryConstants.DegreesToRadians;

        // Sun's geocentric ecliptic position (latitude ~0); Earth's
        // heliocentric position is the negative of that.
        double xEcl = -radiusAu * Math.Cos(lonRad);
        double yEcl = -radiusAu * Math.Sin(lonRad);

        double eps0 = 84381.406 * AstrometryConstants.ArcsecToRadians; // mean obliquity at J2000
        double cosEps = Math.Cos(eps0);
        double sinEps = Math.Sin(eps0);

        double xEq = xEcl;
        double yEq = yEcl * cosEps;
        double zEq = yEcl * sinEps;

        return new Vector3(xEq, yEq, zEq);
    }

    /// <summary>Earth's heliocentric velocity, equatorial J2000 frame, in AU/day, by central difference.</summary>
    public static Vector3 EarthHeliocentricVelocityJ2000(double julianCenturiesTtFromJ2000)
    {
        const double stepDays = 0.02;
        double stepCenturies = stepDays / AstrometryConstants.DaysPerJulianCentury;

        Vector3 rPlus = EarthHeliocentricPositionJ2000(julianCenturiesTtFromJ2000 + stepCenturies);
        Vector3 rMinus = EarthHeliocentricPositionJ2000(julianCenturiesTtFromJ2000 - stepCenturies);

        return (rPlus - rMinus) / (2.0 * stepDays);
    }

    private static double Mod360(double degrees)
    {
        double m = degrees % 360.0;
        return m < 0 ? m + 360.0 : m;
    }
}
