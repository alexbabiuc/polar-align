namespace FreePolarAlign.Imaging.Wcs;

/// <summary>
/// Converts between focal length and plate scale.
///
/// A solve measures the plate scale directly, and the focal length follows from
/// it and the pixel size. That direction is the useful one: focal length is the
/// number a user thinks they know and often does not -- reducers, flatteners and
/// optimistic manufacturer figures all shift it by a few percent -- whereas the
/// solved scale is measured. So the software should learn the focal length from
/// the first successful solve rather than trust what was typed in (see
/// <see cref="EquipmentProfile"/>).
/// </summary>
public static class PlateScale
{
    /// <summary>
    /// Arcseconds per radian divided by 1000, i.e. 206265 / 1000. Converts a
    /// pixel pitch in microns against a focal length in millimetres.
    /// </summary>
    public const double ArcsecondsPerRadianOverThousand = 206.264806247;

    /// <summary>Focal length in millimetres, from pixel pitch and solved plate scale.</summary>
    public static double FocalLengthMillimetres(double pixelPitchMicrons, double scaleArcsecondsPerPixel)
    {
        if (pixelPitchMicrons <= 0.0 || !double.IsFinite(pixelPitchMicrons))
        {
            throw new ArgumentOutOfRangeException(nameof(pixelPitchMicrons), pixelPitchMicrons, "Pixel pitch must be positive.");
        }

        if (scaleArcsecondsPerPixel <= 0.0 || !double.IsFinite(scaleArcsecondsPerPixel))
        {
            throw new ArgumentOutOfRangeException(nameof(scaleArcsecondsPerPixel), scaleArcsecondsPerPixel, "Plate scale must be positive.");
        }

        return ArcsecondsPerRadianOverThousand * pixelPitchMicrons / scaleArcsecondsPerPixel;
    }

    /// <summary>Plate scale in arcseconds per pixel, from pixel pitch and focal length.</summary>
    public static double ScaleArcsecondsPerPixel(double pixelPitchMicrons, double focalLengthMillimetres)
    {
        if (pixelPitchMicrons <= 0.0 || !double.IsFinite(pixelPitchMicrons))
        {
            throw new ArgumentOutOfRangeException(nameof(pixelPitchMicrons), pixelPitchMicrons, "Pixel pitch must be positive.");
        }

        if (focalLengthMillimetres <= 0.0 || !double.IsFinite(focalLengthMillimetres))
        {
            throw new ArgumentOutOfRangeException(nameof(focalLengthMillimetres), focalLengthMillimetres, "Focal length must be positive.");
        }

        return ArcsecondsPerRadianOverThousand * pixelPitchMicrons / focalLengthMillimetres;
    }

    /// <summary>Frame diagonal in degrees, from sensor geometry and focal length.</summary>
    public static double FieldDiagonalDegrees(int widthPixels, int heightPixels, double pixelPitchMicrons, double focalLengthMillimetres)
    {
        double scale = ScaleArcsecondsPerPixel(pixelPitchMicrons, focalLengthMillimetres);
        double diagonalPixels = Math.Sqrt((double)widthPixels * widthPixels + (double)heightPixels * heightPixels);
        return diagonalPixels * scale / 3600.0;
    }
}

/// <summary>
/// What is known about one camera and telescope combination, and the reason the
/// project keeps such a thing at all: a blind solve is expensive, and a scale
/// hint turns it into a fast one. The focal length here starts as whatever the
/// user supplied and is replaced by the solved value after the first success,
/// so the guess is needed once rather than every session.
/// </summary>
/// <param name="FocalLengthMillimetres">
/// Best current estimate. Null when nothing is known yet, which forces a fully
/// blind solve.
/// </param>
/// <param name="IsFocalLengthSolved">
/// False while the focal length is still the user's figure, true once it came
/// from a solve. Worth distinguishing: a user-supplied value deserves a wide
/// scale tolerance because it is frequently several percent out, while a solved
/// one can be trusted to a fraction of a percent.
/// </param>
public sealed record EquipmentProfile(
    string Name,
    double PixelPitchMicrons,
    int WidthPixels,
    int HeightPixels,
    double? FocalLengthMillimetres = null,
    bool IsFocalLengthSolved = false)
{
    /// <summary>
    /// Fractional scale tolerance to give a solver. Wide while the focal length
    /// is only claimed, narrow once measured.
    /// </summary>
    public double ScaleToleranceFraction => IsFocalLengthSolved ? 0.05 : 0.25;

    public double? ExpectedScaleArcsecondsPerPixel => FocalLengthMillimetres is { } focalLength
        ? PlateScale.ScaleArcsecondsPerPixel(PixelPitchMicrons, focalLength)
        : null;

    public double? ExpectedFieldRadiusDegrees => FocalLengthMillimetres is { } focalLength
        ? PlateScale.FieldDiagonalDegrees(WidthPixels, HeightPixels, PixelPitchMicrons, focalLength) / 2.0
        : null;

    /// <summary>
    /// Returns this profile updated with the focal length implied by a solved
    /// plate scale, marked as measured.
    /// </summary>
    public EquipmentProfile WithSolvedScale(double scaleArcsecondsPerPixel) => this with
    {
        FocalLengthMillimetres = PlateScale.FocalLengthMillimetres(PixelPitchMicrons, scaleArcsecondsPerPixel),
        IsFocalLengthSolved = true,
    };
}
