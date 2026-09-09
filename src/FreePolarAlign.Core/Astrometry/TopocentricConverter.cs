namespace FreePolarAlign.Core.Astrometry;

/// <summary>Observer geodetic location (WGS84).</summary>
public sealed record ObserverSite(double LatitudeDegrees, double LongitudeDegrees, double HeightMeters);

/// <summary>A horizontal-coordinate result: azimuth (N=0, E=90) and altitude, both in degrees.</summary>
public readonly record struct HorizontalCoordinates(double AzimuthDegrees, double AltitudeDegrees);

/// <summary>
/// J2000 ICRS RA/Dec to topocentric horizontal coordinates (D5). Implements
/// the classical, equinox-based apparent-place chain:
///
///   1. Annual aberration (<see cref="Aberration"/>), applied directly to the
///      ICRS/GCRS direction vector.
///   2. Frame bias + IAU 2006 precession + IAU 2000B nutation
///      (<see cref="Precession2006"/>, <see cref="Nutation2000B"/>), rotating
///      to the true equator and equinox of date.
///   3. Greenwich Apparent Sidereal Time (from Earth Rotation Angle plus the
///      IAU 2006 GMST polynomial and the equation of the equinoxes) minus
///      apparent RA gives hour angle.
///   4. Hour angle/declination to local horizontal, including polar motion
///      and diurnal aberration, following the same geometry as IAU
///      SOFA/ERFA's <c>iauApio</c>/<c>iauAtioq</c> (ERFA's CIO-based ERA/CIRS
///      quantities are algebraically equivalent to this equinox-based
///      GAST/apparent-RA formulation; only the intermediate quantities
///      differ, not the observed result).
///   5. Optical atmospheric refraction (<see cref="Refraction"/>), a pure
///      altitude correction that leaves azimuth unchanged.
///
/// Polar motion and UT1-UTC are measured inputs
/// (<see cref="EarthOrientationParameters"/>). They default to
/// <see cref="EarthOrientationParameters.Zero"/>, which costs at most about 14
/// arcseconds of pointing but only a fraction of an arcsecond in a polar
/// alignment result, for the reason given on that type. Callers reproducing
/// another implementation to sub-arcsecond pointing agreement must supply real
/// IERS values.
/// </summary>
public static class TopocentricConverter
{
    public static HorizontalCoordinates ToAltAz(
        double raIcrsDegrees,
        double decIcrsDegrees,
        DateTime utc,
        ObserverSite site,
        AtmosphericConditions? atmosphere = null,
        EarthOrientationParameters? earthOrientation = null)
    {
        atmosphere ??= AtmosphericConditions.Vacuum;
        EarthOrientationParameters eop = earthOrientation ?? EarthOrientationParameters.Zero;

        double ttJulianDate = TimeScales.ToTerrestrialTimeJulianDate(utc);
        double t = TimeScales.JulianCenturiesTt(ttJulianDate);
        double ut1JulianDate = TimeScales.ToUt1JulianDate(utc, eop.Ut1MinusUtcSeconds);

        double raIcrsRad = raIcrsDegrees * AstrometryConstants.DegreesToRadians;
        double decIcrsRad = decIcrsDegrees * AstrometryConstants.DegreesToRadians;
        Vector3 icrsDirection = Vector3.FromSpherical(raIcrsRad, decIcrsRad);

        var (deltaPsi, deltaEpsilon) = Nutation2000B.Evaluate(t);
        Matrix3 npb = Precession2006.BuildBiasPrecessionNutationMatrix(t, deltaPsi, deltaEpsilon);

        return ToAltAzInternal(icrsDirection, t, ut1JulianDate, site, atmosphere, eop, npb, deltaPsi);
    }

    /// <summary>
    /// The transform proper, taking the epoch-dependent quantities as arguments
    /// so that <see cref="FromAltAz"/> can iterate against it without rebuilding
    /// the precession-nutation matrix and nutation series on every pass.
    /// </summary>
    private static HorizontalCoordinates ToAltAzInternal(
        Vector3 icrsDirection,
        double t,
        double ut1JulianDate,
        ObserverSite site,
        AtmosphericConditions atmosphere,
        EarthOrientationParameters eop,
        in Matrix3 npb,
        double deltaPsi)
    {
        // --- Step 1: annual aberration, applied to the ICRS direction. ---
        Vector3 properDirection = Aberration.Apply(icrsDirection, t);

        // --- Step 2: frame bias + precession + nutation -> true equator/equinox of date. ---
        Vector3 dateDirection = npb.Apply(properDirection);
        var (raApparentRad, decApparentRad) = dateDirection.ToSpherical();

        // --- Step 3: Greenwich Apparent Sidereal Time -> hour angle. ---
        double gast = GreenwichApparentSiderealTime(ut1JulianDate, t, deltaPsi);
        double elongRad = site.LongitudeDegrees * AstrometryConstants.DegreesToRadians;
        double hourAngle = gast + elongRad - raApparentRad;

        // --- Step 4: hour angle/declination -> local horizontal, with polar motion and diurnal aberration. ---
        double phiRad = site.LatitudeDegrees * AstrometryConstants.DegreesToRadians;
        double xpRad = eop.PolarMotionXArcsec * AstrometryConstants.ArcsecToRadians;
        double ypRad = eop.PolarMotionYArcsec * AstrometryConstants.ArcsecToRadians;
        double era = EarthRotationAngle(ut1JulianDate);
        double sp = -47e-6 * t * AstrometryConstants.ArcsecToRadians;

        var (xpl, ypl) = LocalPolarMotionAngles(era, sp, xpRad, ypRad, elongRad);
        double diurab = DiurnalAberrationMagnitude(phiRad, site.HeightMeters);

        // "-HA, Dec" Cartesian vector (x toward the meridian/equator point, z toward the pole).
        double x = Math.Cos(decApparentRad) * Math.Cos(hourAngle);
        double y = -Math.Cos(decApparentRad) * Math.Sin(hourAngle);
        double z = Math.Sin(decApparentRad);

        double sx = Math.Sin(xpl), cx = Math.Cos(xpl);
        double sy = Math.Sin(ypl), cy = Math.Cos(ypl);
        double xhd = cx * x + sx * z;
        double yhd = sx * sy * x + cy * y - cx * sy * z;
        double zhd = -sx * cy * x + sy * y + cx * cy * z;

        double f = 1.0 - diurab * yhd;
        double xhdt = f * xhd;
        double yhdt = f * (yhd + diurab);
        double zhdt = f * zhd;

        double sphi = Math.Sin(phiRad), cphi = Math.Cos(phiRad);

        // Az/El vector in the internal (South=0, East=90) convention.
        double xaet = sphi * xhdt - cphi * zhdt;
        double yaet = yhdt;
        double zaet = cphi * xhdt + sphi * zhdt;

        // --- Step 5: refraction (altitude only). ---
        var (refA, refB) = Refraction.Coefficients(atmosphere);
        double horizontalRadius = Math.Sqrt(xaet * xaet + yaet * yaet);

        // Floors matching ERFA's eraAtioq (CELMIN, SELMIN): near and below the
        // horizon the "AT + B tan^3(Z)" model is not meaningful, so the
        // zenith-angle terms are clamped to a small *positive* floor rather
        // than tracking the true (possibly negative) sine of altitude. This
        // tapers the correction near the horizon instead of letting it blow
        // up or flip sign below it.
        const double celmin = 1e-6;
        const double selmin = 0.05;
        double rSafe = horizontalRadius > celmin ? horizontalRadius : celmin;
        double zSafe = zaet > selmin ? zaet : selmin;
        double tanZ = rSafe / zSafe;
        double w = refB * tanZ * tanZ;
        double delta = (refA + w) * tanZ / (1.0 + (refA + 3.0 * w) / (zSafe * zSafe));
        double cosDelta = 1.0 - delta * delta / 2.0;
        double fRefr = cosDelta - delta * zSafe / rSafe;

        double xaeo = xaet * fRefr;
        double yaeo = yaet * fRefr;
        double zaeo = cosDelta * zaet + delta * rSafe;

        double azimuthRad = NormalizeAngle(Math.Atan2(yaeo, -xaeo));
        double altitudeRad = Math.Atan2(zaeo, Math.Sqrt(xaeo * xaeo + yaeo * yaeo));

        return new HorizontalCoordinates(
            azimuthRad * AstrometryConstants.RadiansToDegrees,
            altitudeRad * AstrometryConstants.RadiansToDegrees);
    }

    /// <summary>
    /// The inverse of <see cref="ToAltAz"/>: what to point the telescope at, in
    /// J2000 ICRS, to look in a given direction from the ground.
    ///
    /// Needed wherever a constraint is naturally expressed in the horizon frame
    /// but has to be commanded in equatorial coordinates -- target selection
    /// above a minimum altitude (Phase 3), and generating what a camera would
    /// see for a given mount pointing in the virtual observatory.
    ///
    /// Most of the forward chain is a rotation and so is trivially invertible;
    /// aberration and refraction are not, being a displacement toward the
    /// direction of travel and a compression toward the zenith respectively. So
    /// this inverts the rotations analytically for a starting estimate, then
    /// corrects it against the full forward transform. Each correction applies
    /// the rotation that carries the current result onto the target, which
    /// converges quickly because the residual map is close to the identity.
    /// </summary>
    public static (double RaIcrsDegrees, double DecIcrsDegrees) FromAltAz(
        HorizontalCoordinates target,
        DateTime utc,
        ObserverSite site,
        AtmosphericConditions? atmosphere = null,
        EarthOrientationParameters? earthOrientation = null)
    {
        atmosphere ??= AtmosphericConditions.Vacuum;
        EarthOrientationParameters eop = earthOrientation ?? EarthOrientationParameters.Zero;

        double ttJulianDate = TimeScales.ToTerrestrialTimeJulianDate(utc);
        double t = TimeScales.JulianCenturiesTt(ttJulianDate);
        double ut1JulianDate = TimeScales.ToUt1JulianDate(utc, eop.Ut1MinusUtcSeconds);

        var (deltaPsi, deltaEpsilon) = Nutation2000B.Evaluate(t);
        Matrix3 npb = Precession2006.BuildBiasPrecessionNutationMatrix(t, deltaPsi, deltaEpsilon);
        double gast = GreenwichApparentSiderealTime(ut1JulianDate, t, deltaPsi);
        double elongRad = site.LongitudeDegrees * AstrometryConstants.DegreesToRadians;
        double phiRad = site.LatitudeDegrees * AstrometryConstants.DegreesToRadians;

        Vector3 targetVector = FromHorizontal(target);

        // The correction is applied to what is *asked of* the rotation-only
        // inverse, not to its output. Both the error and the request then live
        // in the horizon frame, which is the point: an earlier version rotated
        // the ICRS estimate by an axis computed from horizon-frame vectors, and
        // since the two frames differ by a large rotation, that correction had
        // the right magnitude and an almost arbitrary direction.
        //
        // Because the rotation-only inverse is already accurate to about an
        // arcminute, asking it for a point displaced by the residual converges
        // in two or three passes.
        Vector3 requested = targetVector;
        Vector3 estimate = InvertRotations(requested, npb, gast, elongRad, phiRad);

        for (int iteration = 0; iteration < 16; iteration++)
        {
            HorizontalCoordinates achieved = ToAltAzInternal(estimate, t, ut1JulianDate, site, atmosphere, eop, npb, deltaPsi);
            Vector3 achievedVector = FromHorizontal(achieved);

            Vector3 axis = achievedVector.Cross(targetVector);
            double sine = axis.Length;
            double angle = Math.Atan2(sine, achievedVector.Dot(targetVector));
            if (angle < 1e-13 || sine <= 0)
            {
                break;
            }

            requested = RotateAbout(requested, axis / sine, angle).Normalized();
            estimate = InvertRotations(requested, npb, gast, elongRad, phiRad);
        }

        var (ra, dec) = estimate.ToSpherical();
        return (ra * AstrometryConstants.RadiansToDegrees, dec * AstrometryConstants.RadiansToDegrees);
    }

    /// <summary>
    /// Undoes only the rotational part of the forward chain -- horizon frame,
    /// hour angle and precession-nutation -- ignoring refraction, aberration and
    /// polar motion. Good to roughly an arcminute, which is a starting estimate
    /// rather than an answer.
    /// </summary>
    private static Vector3 InvertRotations(Vector3 horizontal, Matrix3 npb, double gast, double elongRad, double phiRad)
    {
        double sphi = Math.Sin(phiRad), cphi = Math.Cos(phiRad);

        // Horizon (north/east/up) back to the internal south-referenced az/el
        // frame the forward transform works in.
        double xaet = -horizontal.X;
        double yaet = horizontal.Y;
        double zaet = horizontal.Z;

        double xhd = sphi * xaet + cphi * zaet;
        double yhd = yaet;
        double zhd = -cphi * xaet + sphi * zaet;

        double declination = Math.Atan2(zhd, Math.Sqrt(xhd * xhd + yhd * yhd));
        double hourAngle = Math.Atan2(-yhd, xhd);
        double rightAscension = gast + elongRad - hourAngle;

        Vector3 apparent = Vector3.FromSpherical(rightAscension, declination);
        return Transpose(npb).Apply(apparent).Normalized();
    }

    private static Matrix3 Transpose(in Matrix3 m) => new(
        m.Get(0, 0), m.Get(1, 0), m.Get(2, 0),
        m.Get(0, 1), m.Get(1, 1), m.Get(2, 1),
        m.Get(0, 2), m.Get(1, 2), m.Get(2, 2));

    private static Vector3 RotateAbout(Vector3 v, Vector3 axis, double angleRadians)
    {
        double c = Math.Cos(angleRadians);
        double s = Math.Sin(angleRadians);
        return c * v + s * axis.Cross(v) + ((1.0 - c) * axis.Dot(v)) * axis;
    }

    private static Vector3 FromHorizontal(HorizontalCoordinates coordinates)
    {
        double altitude = coordinates.AltitudeDegrees * AstrometryConstants.DegreesToRadians;
        double azimuth = coordinates.AzimuthDegrees * AstrometryConstants.DegreesToRadians;
        double cosAltitude = Math.Cos(altitude);
        return new Vector3(
            cosAltitude * Math.Cos(azimuth),
            cosAltitude * Math.Sin(azimuth),
            Math.Sin(altitude));
    }

    private static double EarthRotationAngle(double ut1JulianDate)
    {
        double du = ut1JulianDate - AstrometryConstants.JulianDateJ2000;
        double turns = 0.7790572732640 + 1.00273781191135448 * du;
        return NormalizeAngle(AstrometryConstants.TwoPi * Fraction(turns));
    }

    private static double GreenwichMeanSiderealTime(double ut1JulianDate, double tCenturiesTt)
    {
        double era = EarthRotationAngle(ut1JulianDate);
        double t = tCenturiesTt;
        double polyArcsec = 0.014506
            + (4612.156534
            + (1.3915817
            + (-0.00000044
            + (-0.000029956
            + (-0.0000000368) * t) * t) * t) * t) * t;

        return NormalizeAngle(era + polyArcsec * AstrometryConstants.ArcsecToRadians);
    }

    private static double GreenwichApparentSiderealTime(double ut1JulianDate, double tCenturiesTt, double deltaPsi)
    {
        double gmst = GreenwichMeanSiderealTime(ut1JulianDate, tCenturiesTt);
        double epsA = Precession2006.MeanObliquity(tCenturiesTt);
        var args = FundamentalArguments.Evaluate(tCenturiesTt);
        double omega = args.MeanLongitudeAscendingNodeMoon;

        double complementaryArcsec = 0.00264 * Math.Sin(omega) + 0.000063 * Math.Sin(2.0 * omega);
        double equationOfEquinoxes = deltaPsi * Math.Cos(epsA) + complementaryArcsec * AstrometryConstants.ArcsecToRadians;

        return NormalizeAngle(gmst + equationOfEquinoxes);
    }

    /// <summary>
    /// Global polar motion (xp, yp) resolved into angles local to the
    /// observer's meridian, following IAU SOFA/ERFA's <c>iauApio</c>.
    /// </summary>
    private static (double Xpl, double Ypl) LocalPolarMotionAngles(double era, double sp, double xp, double yp, double elongRad)
    {
        Matrix3 r = Matrix3.RotateZ(era + sp);
        r = Matrix3.RotateY(-xp).Multiply(r);
        r = Matrix3.RotateX(-yp).Multiply(r);
        r = Matrix3.RotateZ(elongRad).Multiply(r);

        double a = r.Get(0, 0);
        double b = r.Get(0, 1);
        double c = r.Get(0, 2);
        double xpl = Math.Atan2(c, Math.Sqrt(a * a + b * b));

        double a2 = r.Get(1, 2);
        double b2 = r.Get(2, 2);
        double ypl = (a2 != 0.0 || b2 != 0.0) ? -Math.Atan2(a2, b2) : 0.0;

        return (xpl, ypl);
    }

    /// <summary>
    /// Magnitude of the diurnal aberration vector (v/c) for a fixed point on
    /// the WGS84 ellipsoid at the given geodetic latitude/height. The
    /// sub-meter shift polar motion would apply to the observer's position is
    /// negligible here: diurnal aberration itself totals only ~0.3 arcsec, so
    /// a relative error at the 1e-4 level in its magnitude is far below the
    /// budget this project is working to.
    /// </summary>
    private static double DiurnalAberrationMagnitude(double phiRad, double heightMeters)
    {
        const double wgs84SemiMajorAxisMeters = 6378137.0;
        const double wgs84Flattening = 1.0 / 298.257223563;
        const double earthAngularVelocityRadPerSec = 1.00273781191135448 * AstrometryConstants.TwoPi / 86400.0;
        const double speedOfLightMetersPerSec = 299792458.0;

        double sp = Math.Sin(phiRad), cp = Math.Cos(phiRad);
        double wFactor = 1.0 - wgs84Flattening;
        wFactor *= wFactor;
        double d = cp * cp + wFactor * sp * sp;
        double ac = wgs84SemiMajorAxisMeters / Math.Sqrt(d);
        double distanceFromAxisMeters = (ac + heightMeters) * cp;

        return earthAngularVelocityRadPerSec * distanceFromAxisMeters / speedOfLightMetersPerSec;
    }

    private static double Fraction(double x) => x - Math.Floor(x);

    private static double NormalizeAngle(double radians)
    {
        double r = radians % AstrometryConstants.TwoPi;
        return r < 0 ? r + AstrometryConstants.TwoPi : r;
    }
}
