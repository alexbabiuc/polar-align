namespace FreePolarAlign.Core.Astrometry;

/// <summary>
/// The Delaunay fundamental arguments used by the IAU 2000B nutation series,
/// from Simon et al. (1994), as reproduced in IAU SOFA's <c>iauNut00b</c>.
/// </summary>
internal readonly struct FundamentalArguments
{
    public readonly double MeanAnomalyMoon;
    public readonly double MeanAnomalySun;
    public readonly double MeanArgumentOfLatitudeMoon;
    public readonly double MeanElongationMoonFromSun;
    public readonly double MeanLongitudeAscendingNodeMoon;

    private FundamentalArguments(double l, double lp, double f, double d, double om)
    {
        MeanAnomalyMoon = l;
        MeanAnomalySun = lp;
        MeanArgumentOfLatitudeMoon = f;
        MeanElongationMoonFromSun = d;
        MeanLongitudeAscendingNodeMoon = om;
    }

    public static FundamentalArguments Evaluate(double tCenturiesTt)
    {
        double t = tCenturiesTt;
        double l = Reduce(485868.249036 + 1717915923.2178 * t);
        double lp = Reduce(1287104.79305 + 129596581.0481 * t);
        double f = Reduce(335779.526232 + 1739527262.8478 * t);
        double d = Reduce(1072260.70369 + 1602961601.2090 * t);
        double om = Reduce(450160.398036 - 6962890.5431 * t);
        return new FundamentalArguments(l, lp, f, d, om);
    }

    private static double Reduce(double arcseconds)
    {
        double wrapped = arcseconds % AstrometryConstants.ArcsecPerTurn;
        return wrapped * AstrometryConstants.ArcsecToRadians;
    }
}
