using FreePolarAlign.Imaging.Wcs;

namespace FreePolarAlign.Solving;

/// <summary>
/// Turns what an external solver reports into the solver-agnostic solution.
/// Two shapes of report arrive: a full WCS (ASTAP), and a centre, scale and
/// rotation (ASPS, PlateSolve3), for which the CD matrix has to be rebuilt.
/// </summary>
internal static class SolutionGeometry
{
    public static PlateSolveSolution FromWcs(TanWcsSolution wcs, int imageWidth, int imageHeight, int matchedStarCount, TimeSpan elapsed)
    {
        // The image center, not CRVAL, is the reported center: a solver is not
        // guaranteed to anchor CRPIX at the image's midpoint, and re-using the
        // existing Imaging WCS math here is exact regardless of where it did.
        (double centerRa, double centerDec) = wcs.PixelToWorld(imageWidth / 2.0 + 0.5, imageHeight / 2.0 + 0.5);

        double scaleXArcsecPerPixel = 3600.0 * Math.Sqrt(wcs.Cd1_1 * wcs.Cd1_1 + wcs.Cd2_1 * wcs.Cd2_1);
        double scaleYArcsecPerPixel = 3600.0 * Math.Sqrt(wcs.Cd1_2 * wcs.Cd1_2 + wcs.Cd2_2 * wcs.Cd2_2);
        double pixelScale = 0.5 * (scaleXArcsecPerPixel + scaleYArcsecPerPixel);

        // Rotation is not reported directly; derive it from the CD matrix rather
        // than invent a number, the same source D12 uses for parity. For the
        // standard CD <-> CDELT/CROTA2 relation (FITS paper II, Calabretta &
        // Greisen: CD1_2 = -CDELT2*sin(CROTA2), CD2_2 = CDELT2*cos(CROTA2),
        // CDELT2 > 0 by convention), atan2(-CD1_2, CD2_2) recovers CROTA2
        // exactly, including the unrotated case (0 degrees).
        double rotationDegrees = Math.Atan2(-wcs.Cd1_2, wcs.Cd2_2) * (180.0 / Math.PI);

        return new PlateSolveSolution(
            CenterRaDegrees: centerRa,
            CenterDecDegrees: centerDec,
            PixelScaleArcsecPerPixel: pixelScale,
            RotationDegrees: rotationDegrees,
            Cd1_1: wcs.Cd1_1,
            Cd1_2: wcs.Cd1_2,
            Cd2_1: wcs.Cd2_1,
            Cd2_2: wcs.Cd2_2,
            MatchedStarCount: matchedStarCount,
            SolveDuration: elapsed);
    }

    /// <summary>
    /// The CD matrix for a frame of a given scale and CROTA2, by the same
    /// FITS paper II relation <see cref="FromWcs"/> inverts. Unmirrored means
    /// east to the left of north, CDELT1 negative and a negative determinant,
    /// which is how a camera on a refractor or reflector without a diagonal
    /// sees the sky.
    /// </summary>
    public static (double Cd1_1, double Cd1_2, double Cd2_1, double Cd2_2) CdMatrix(
        double scaleArcsecPerPixel, double crota2Degrees, bool mirrored)
    {
        double cdelt2 = scaleArcsecPerPixel / 3600.0;
        double cdelt1 = mirrored ? cdelt2 : -cdelt2;
        double angle = crota2Degrees * Math.PI / 180.0;
        double cos = Math.Cos(angle);
        double sin = Math.Sin(angle);

        return (cdelt1 * cos, -cdelt2 * sin, cdelt1 * sin, cdelt2 * cos);
    }
}
