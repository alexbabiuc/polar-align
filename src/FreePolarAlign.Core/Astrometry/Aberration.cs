namespace FreePolarAlign.Core.Astrometry;

/// <summary>
/// Annual aberration of light (~20.5 arcsec, D5: "matters for the 1 arcsec
/// threshold -- do not skip it"), applied as a first-order correction to a
/// GCRS direction vector using Earth's heliocentric velocity. The classical
/// formula p' = p + beta - (p.beta)p is accurate to O(beta^2) ~ 2e-8 radians
/// (~0.004 arcsec) -- negligible against the 1 arcsecond budget, so the full
/// relativistic formula (as in ERFA's eraAb, which also folds in light
/// deflection) is not needed here.
/// </summary>
internal static class Aberration
{
    public static Vector3 Apply(Vector3 direction, double julianCenturiesTtFromJ2000)
    {
        Vector3 velocityAuPerDay = SolarPosition.EarthHeliocentricVelocityJ2000(julianCenturiesTtFromJ2000);
        Vector3 beta = velocityAuPerDay / AstrometryConstants.SpeedOfLightAuPerDay;

        double pDotBeta = direction.Dot(beta);
        Vector3 aberrated = direction + beta - pDotBeta * direction;
        return aberrated.Normalized();
    }
}
