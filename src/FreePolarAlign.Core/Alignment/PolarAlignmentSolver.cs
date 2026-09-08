using FreePolarAlign.Core.Astrometry;

namespace FreePolarAlign.Core.Alignment;

/// <summary>
/// A complete polar alignment measurement: how far the mount's axis is from the
/// pole, how well that is known, and whether it should be shown to the user at
/// all.
/// </summary>
/// <param name="TotalErrorArcminutes">
/// Great-circle angle between the mount axis and the pole. This is the figure
/// the 10 arcminute success indication is judged on, because it is the one that
/// governs field rotation and drift. It is not the sum of the two bolt figures,
/// and is smaller than the azimuth figure alone whenever the axis sits high:
/// total is approximately sqrt(altitude^2 + (azimuth * cos(altitude))^2).
/// </param>
/// <param name="AzimuthErrorArcminutes">
/// Error in the azimuth *angle* -- what an azimuth bolt changes. Larger than
/// the corresponding on-sky angle by 1/cos(axis altitude) (D12), so at high
/// latitudes a small on-sky error still demands noticeable bolt travel.
/// </param>
/// <param name="IsTrustworthy">
/// False when the fit's residuals are inconsistent with the expected solve
/// noise, or the geometry is too ill-conditioned to support the answer. A
/// confident wrong number is the worst outcome this software can produce, since
/// the user acts on it and has no way to notice (D11), so the caller must
/// withhold rather than display when this is false.
/// </param>
public sealed record PolarAlignmentSolution(
    HorizontalCoordinates MountAxis,
    HorizontalCoordinates NominalPole,
    double AltitudeErrorArcminutes,
    double AzimuthErrorArcminutes,
    double TotalErrorArcminutes,
    double AltitudeSigmaArcminutes,
    double AzimuthSigmaArcminutes,
    double TotalSigmaArcminutes,
    SmallCircleFit Fit,
    bool IsTrustworthy,
    string? UntrustworthyReason)
{
    /// <summary>How far to move the axis in altitude to correct it -- the negated error.</summary>
    public double AltitudeCorrectionArcminutes => -AltitudeErrorArcminutes;

    /// <summary>How far to rotate the mount in azimuth to correct it -- the negated error.</summary>
    public double AzimuthCorrectionArcminutes => -AzimuthErrorArcminutes;
}

/// <summary>
/// Turns solved pointing directions into a polar alignment measurement.
/// </summary>
public static class PolarAlignmentSolver
{
    private const double RadiansToArcminutes = 180.0 * 60.0 / Math.PI;
    private const double ArcsecondsToArcminutes = 1.0 / 60.0;

    /// <summary>
    /// Residuals larger than this multiple of the expected solve noise, in RMS,
    /// mean the observations do not lie on one circle. The usual cause is the
    /// operator error the README singles out -- declination moved between
    /// captures, or the mount crossed the meridian -- which breaks the
    /// rigid-body assumption and still yields a plausible-looking answer
    /// (D8, D11). Expressed as reduced chi-square, so it is a factor on the
    /// noise, squared.
    /// </summary>
    public const double ReducedChiSquareLimit = 9.0;

    /// <param name="observations">
    /// Physical pointing directions in the horizon frame, one per capture --
    /// each obtained by putting a plate solve through the full apparent-place
    /// transform, refraction included.
    /// </param>
    public static PolarAlignmentSolution Solve(
        IReadOnlyList<HorizontalCoordinates> observations,
        double siteLatitudeDegrees,
        double expectedNoiseArcseconds)
    {
        HorizontalCoordinates pole = MountForwardModel.NominalPole(siteLatitudeDegrees);
        SmallCircleFit fit = SmallCircleFitter.Fit(observations, pole, expectedNoiseArcseconds);

        double altitudeError = (fit.Axis.AltitudeDegrees - pole.AltitudeDegrees) * 60.0;
        double azimuthError = WrapDegrees(fit.Axis.AzimuthDegrees - pole.AzimuthDegrees) * 60.0;

        Vector3 axisVector = SmallCircleFitter.ToHorizonVector(fit.Axis);
        Vector3 poleVector = SmallCircleFitter.ToHorizonVector(pole);
        double totalError = AngleBetween(axisVector, poleVector) * RadiansToArcminutes;

        double sigmaAltitude = fit.Uncertainty.AltitudeSigmaArcseconds * ArcsecondsToArcminutes;
        double sigmaAzimuth = fit.Uncertainty.AzimuthAngleSigmaArcseconds * ArcsecondsToArcminutes;
        double sigmaTotal = TotalSigma(fit, altitudeError, azimuthError);

        var (isTrustworthy, reason) = AssessTrust(fit);

        return new PolarAlignmentSolution(
            fit.Axis,
            pole,
            altitudeError,
            azimuthError,
            totalError,
            sigmaAltitude,
            sigmaAzimuth,
            sigmaTotal,
            fit,
            isTrustworthy,
            reason);
    }

    /// <summary>
    /// Uncertainty along the direction the error actually points, from the
    /// on-sky covariance ellipse. Where the axis is already on the pole that
    /// direction is undefined, so the ellipse's major axis is reported instead
    /// -- the conservative choice, and the honest one, since the measurement
    /// genuinely does not know which way a near-zero error leans.
    /// </summary>
    private static double TotalSigma(SmallCircleFit fit, double altitudeErrorArcminutes, double azimuthErrorArcminutes)
    {
        double sigmaAltitude = fit.Uncertainty.AltitudeSigmaArcseconds * ArcsecondsToArcminutes;
        double sigmaAzimuthOnSky = fit.Uncertainty.AzimuthOnSkySigmaArcseconds * ArcsecondsToArcminutes;

        if (!double.IsFinite(sigmaAltitude) || !double.IsFinite(sigmaAzimuthOnSky))
        {
            return double.PositiveInfinity;
        }

        double covariance = fit.Uncertainty.Correlation * sigmaAltitude * sigmaAzimuthOnSky;

        double cosAltitude = Math.Cos(fit.Axis.AltitudeDegrees * Math.PI / 180.0);
        double altitudeComponent = altitudeErrorArcminutes;
        double azimuthComponent = azimuthErrorArcminutes * cosAltitude;
        double magnitude = Math.Sqrt(altitudeComponent * altitudeComponent + azimuthComponent * azimuthComponent);

        if (magnitude < 1e-9)
        {
            double trace = sigmaAltitude * sigmaAltitude + sigmaAzimuthOnSky * sigmaAzimuthOnSky;
            double determinant = sigmaAltitude * sigmaAltitude * sigmaAzimuthOnSky * sigmaAzimuthOnSky - covariance * covariance;
            double largest = 0.5 * (trace + Math.Sqrt(Math.Max(trace * trace - 4.0 * determinant, 0.0)));
            return Math.Sqrt(Math.Max(largest, 0.0));
        }

        double ua = altitudeComponent / magnitude;
        double ub = azimuthComponent / magnitude;
        double variance = ua * ua * sigmaAltitude * sigmaAltitude
                        + 2.0 * ua * ub * covariance
                        + ub * ub * sigmaAzimuthOnSky * sigmaAzimuthOnSky;

        return Math.Sqrt(Math.Max(variance, 0.0));
    }

    private static (bool IsTrustworthy, string? Reason) AssessTrust(SmallCircleFit fit)
    {
        if (!fit.IsWellConditioned)
        {
            return (false, $"Geometry is too ill-conditioned to solve (condition number {fit.Uncertainty.ConditionNumber:G3}). " +
                           "Widen the RA sweep between captures.");
        }

        if (!fit.IsCircleResolved)
        {
            return (false, $"The captures do not trace a resolvable circle (curvature signal {fit.CurvatureSignalToNoise:F1}, " +
                           $"needs {SmallCircleFitter.CurvatureSignalToNoiseLimit:F0}): at a fitted radius of " +
                           $"{fit.RadiusDegrees * 60.0:F1}' the arc cannot be told from a straight line. " +
                           "Point further from the mount's polar axis.");
        }

        if (fit.DegreesOfFreedom > 0)
        {
            double reducedChiSquare = fit.ChiSquare / fit.DegreesOfFreedom;
            if (reducedChiSquare > ReducedChiSquareLimit)
            {
                return (false, $"Residuals are {Math.Sqrt(reducedChiSquare):F1}x the expected solve noise " +
                               $"(RMS {fit.ResidualRmsArcseconds:F1}\"), so the captures do not lie on one circle. " +
                               "Declination probably moved between captures, or the mount crossed the meridian.");
            }
        }

        return (true, null);
    }

    private static double AngleBetween(Vector3 a, Vector3 b)
    {
        Vector3 cross = a.Cross(b);
        return Math.Atan2(cross.Length, a.Dot(b));
    }

    private static double WrapDegrees(double degrees)
    {
        double wrapped = degrees % 360.0;
        if (wrapped > 180.0)
        {
            wrapped -= 360.0;
        }
        else if (wrapped <= -180.0)
        {
            wrapped += 360.0;
        }

        return wrapped;
    }
}
