namespace FreePolarAlign.Core.Astrometry;

/// <summary>
/// J2000 ICRS RA/Dec to and from the apparent place of date -- the equatorial
/// system mounts and drivers call "JNow", and ASCOM calls
/// <c>equTopocentric</c>.
///
/// This is the first half of <see cref="TopocentricConverter"/>'s chain, stopped
/// one step early: annual aberration applied to the ICRS direction, then frame
/// bias + IAU 2006 precession + IAU 2000B nutation onto the true equator and
/// equinox of date. The remaining steps of that chain (hour angle, the horizon
/// rotation, polar motion, diurnal aberration and refraction) are exactly what
/// a mount does for itself once it has been handed coordinates of date, so they
/// are deliberately *not* applied here -- applying them would be applying them
/// twice.
///
/// What is left out, and why it is safe to leave out: strictly, "topocentric"
/// also includes diurnal aberration (~0.3 arcsec) and, for solar-system bodies,
/// diurnal parallax (nil for stars). Both are below the noise of a mount's own
/// pointing, and both are already accounted for on the *other* side of the
/// conversion by whatever the mount does with the coordinates. Refraction is
/// excluded for the same reason it is excluded from
/// <c>TargetSelection.ResolveCommand</c> (D16): a mount's pointing model is
/// geometric, so this transform has to be the exact inverse of what the mount
/// will do, not the most physically complete transform available.
/// </summary>
public static class ApparentPlace
{
    /// <summary>
    /// Iterations used to undo aberration in <see cref="ToJ2000"/>. The
    /// correction is of order |v|/c ~ 1e-4 radians, so each pass reduces the
    /// residual by that factor; three passes land far below double precision.
    /// </summary>
    private const int AberrationInversionIterations = 3;

    /// <summary>J2000 ICRS to the apparent (JNow) place at <paramref name="utc"/>.</summary>
    public static (double RaDegrees, double DecDegrees) FromJ2000(double raJ2000Degrees, double decJ2000Degrees, DateTime utc)
    {
        double t = JulianCenturiesOf(utc);
        var (deltaPsi, deltaEpsilon) = Nutation2000B.Evaluate(t);
        Matrix3 npb = Precession2006.BuildBiasPrecessionNutationMatrix(t, deltaPsi, deltaEpsilon);

        Vector3 icrs = Vector3.FromSpherical(
            raJ2000Degrees * AstrometryConstants.DegreesToRadians,
            decJ2000Degrees * AstrometryConstants.DegreesToRadians);

        Vector3 apparent = npb.Apply(Aberration.Apply(icrs, t));
        return ToDegrees(apparent);
    }

    /// <summary>
    /// The apparent (JNow) place at <paramref name="utc"/> back to J2000 ICRS --
    /// the exact inverse of <see cref="FromJ2000"/>.
    ///
    /// The rotations invert analytically; aberration does not, being a
    /// displacement toward the direction of travel, so it is removed by fixed
    /// point iteration against the forward correction (the same approach ERFA's
    /// <c>eraAticq</c> takes).
    /// </summary>
    public static (double RaDegrees, double DecDegrees) ToJ2000(double raApparentDegrees, double decApparentDegrees, DateTime utc)
    {
        double t = JulianCenturiesOf(utc);
        var (deltaPsi, deltaEpsilon) = Nutation2000B.Evaluate(t);
        Matrix3 npb = Precession2006.BuildBiasPrecessionNutationMatrix(t, deltaPsi, deltaEpsilon);

        Vector3 apparent = Vector3.FromSpherical(
            raApparentDegrees * AstrometryConstants.DegreesToRadians,
            decApparentDegrees * AstrometryConstants.DegreesToRadians);

        // Undo the rotations, leaving the aberrated ICRS direction.
        Vector3 aberrated = Transpose(npb).Apply(apparent).Normalized();

        Vector3 estimate = aberrated;
        for (int i = 0; i < AberrationInversionIterations; i++)
        {
            estimate = (estimate + (aberrated - Aberration.Apply(estimate, t))).Normalized();
        }

        return ToDegrees(estimate);
    }

    private static double JulianCenturiesOf(DateTime utc) =>
        TimeScales.JulianCenturiesTt(TimeScales.ToTerrestrialTimeJulianDate(utc));

    private static (double RaDegrees, double DecDegrees) ToDegrees(Vector3 direction)
    {
        var (ra, dec) = direction.ToSpherical();
        return (ra * AstrometryConstants.RadiansToDegrees, dec * AstrometryConstants.RadiansToDegrees);
    }

    private static Matrix3 Transpose(in Matrix3 m) => new(
        m.Get(0, 0), m.Get(1, 0), m.Get(2, 0),
        m.Get(0, 1), m.Get(1, 1), m.Get(2, 1),
        m.Get(0, 2), m.Get(1, 2), m.Get(2, 2));
}
