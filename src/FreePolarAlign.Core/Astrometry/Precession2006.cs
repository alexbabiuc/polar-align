namespace FreePolarAlign.Core.Astrometry;

/// <summary>
/// IAU 2006 (P03) precession, in the Fukushima-Williams 4-angle parameterization
/// (Hilton et al. 2006; Capitaine &amp; Wallace 2006), combined here with IAU 2000B
/// nutation to build the frame bias + precession + nutation matrix directly
/// (D5's "equinox-based apparent place... classical chain"). Polynomial
/// coefficients transcribed from IAU SOFA's <c>iauPfw06</c>/<c>iauObl06</c>
/// (equivalently ERFA's <c>eraPfw06</c>/<c>eraObl06</c>).
///
/// This parameterization already folds the GCRS-to-J2000 frame bias into the
/// gamma-bar/phi-bar/psi-bar angles (they run from GCRS, not from the J2000
/// mean equator), so no separate bias matrix is needed -- see Capitaine &amp;
/// Wallace (2006) section 3.
/// </summary>
internal static class Precession2006
{
    /// <summary>Mean obliquity of the ecliptic, IAU 2006, radians.</summary>
    public static double MeanObliquity(double t)
    {
        double arcsec = 84381.406
            + (-46.836769
            + (-0.0001831
            + (0.00200340
            + (-0.000000576
            + (-0.0000000434) * t) * t) * t) * t) * t;
        return arcsec * AstrometryConstants.ArcsecToRadians;
    }

    private static double GammaBar(double t)
    {
        double arcsec = -0.052928
            + (10.556378
            + (0.4932044
            + (-0.00031238
            + (-0.000002788
            + (0.0000000260) * t) * t) * t) * t) * t;
        return arcsec * AstrometryConstants.ArcsecToRadians;
    }

    private static double PhiBar(double t)
    {
        double arcsec = 84381.412819
            + (-46.811016
            + (0.0511268
            + (0.00053289
            + (-0.000000440
            + (-0.0000000176) * t) * t) * t) * t) * t;
        return arcsec * AstrometryConstants.ArcsecToRadians;
    }

    private static double PsiBar(double t)
    {
        double arcsec = -0.041775
            + (5038.481484
            + (1.5584175
            + (-0.00018522
            + (-0.000026452
            + (-0.0000000148) * t) * t) * t) * t) * t;
        return arcsec * AstrometryConstants.ArcsecToRadians;
    }

    /// <summary>
    /// Builds the matrix taking a GCRS direction vector to the true equator
    /// and equinox of date: <c>p_date = M * p_gcrs</c>. Matches ERFA's
    /// <c>eraFw2m</c> applied to the nutation-adjusted psi/eps angles.
    /// </summary>
    public static Matrix3 BuildBiasPrecessionNutationMatrix(double tCenturiesTt, double deltaPsi, double deltaEpsilon)
    {
        double gamb = GammaBar(tCenturiesTt);
        double phib = PhiBar(tCenturiesTt);
        double psib = PsiBar(tCenturiesTt);
        double epsa = MeanObliquity(tCenturiesTt);

        Matrix3 r = Matrix3.RotateZ(gamb);
        r = Matrix3.RotateX(phib).Multiply(r);
        r = Matrix3.RotateZ(-(psib + deltaPsi)).Multiply(r);
        r = Matrix3.RotateX(-(epsa + deltaEpsilon)).Multiply(r);
        return r;
    }
}
