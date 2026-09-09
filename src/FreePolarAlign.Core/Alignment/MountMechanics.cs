using FreePolarAlign.Core.Astrometry;

namespace FreePolarAlign.Core.Alignment;

/// <summary>
/// Converts between a pointing direction in the local horizon frame and the two
/// mechanical angles a German equatorial mount actually has: rotation about its
/// polar axis, and declination away from its equator.
///
/// The pair exists so the same mechanical angles can be *read off* an ideally
/// aligned instrument and then *driven* on a misaligned one. That difference is
/// precisely what polar misalignment is, and expressing it this way keeps it in
/// one place instead of scattered through a simulator.
///
/// Both directions use the same frame convention -- the mount's own meridian,
/// meaning local vertical projected perpendicular to its polar axis, taken as
/// rotation zero -- so with no misalignment and no cone error, composing a
/// decomposition returns the original direction exactly.
/// </summary>
public static class MountMechanics
{
    private const double DegreesToRadians = Math.PI / 180.0;
    private const double RadiansToDegrees = 180.0 / Math.PI;
    private const double ArcminutesToDegrees = 1.0 / 60.0;

    /// <summary>
    /// The mechanical angles an ideally aligned mount at this latitude would
    /// need in order to point in the given direction.
    /// </summary>
    public static (double RotationDegrees, double DeclinationDegrees) Decompose(
        double siteLatitudeDegrees,
        HorizontalCoordinates direction)
    {
        Vector3 axis = ToVector(MountForwardModel.NominalPole(siteLatitudeDegrees));
        var (meridian, perpendicular) = MountFrame(axis);
        Vector3 pointing = ToVector(direction);

        double alongAxis = Math.Clamp(pointing.Dot(axis), -1.0, 1.0);
        double declination = Math.Asin(alongAxis) * RadiansToDegrees;
        double rotation = Math.Atan2(pointing.Dot(perpendicular), pointing.Dot(meridian)) * RadiansToDegrees;

        return (rotation, declination);
    }

    /// <summary>
    /// Where a mount with the given misalignment actually points when driven to
    /// the given mechanical angles.
    /// </summary>
    public static HorizontalCoordinates Compose(
        double siteLatitudeDegrees,
        MountMisalignment misalignment,
        double rotationDegrees,
        double declinationDegrees,
        double coneErrorArcminutes = 0.0,
        double conePhaseDegrees = 0.0)
    {
        Vector3 axis = ToVector(MountForwardModel.MountAxis(siteLatitudeDegrees, misalignment));
        var (meridian, perpendicular) = MountFrame(axis);

        double rotation = rotationDegrees * DegreesToRadians;
        double declination = declinationDegrees * DegreesToRadians;

        // Built at rotation zero, then rotated rigidly about the axis. The order
        // matters: cone error is a fixed offset *within the rotating mount
        // frame*, so it has to be applied before the rotation and carried
        // through it.
        //
        // Applying it afterwards, in a basis derived from the current pointing,
        // silently breaks the rigid-body property the whole method rests on --
        // the offset direction then drifts as the mount turns, the track stops
        // being a circle, and the fit sees residuals it cannot explain.
        // Measured, 20 arcminutes of cone error applied that way varied the
        // radius by 10 arcminutes across a 70 degree sweep.
        Vector3 atZero = (Math.Cos(declination) * meridian + Math.Sin(declination) * axis).Normalized();

        double cone = coneErrorArcminutes * ArcminutesToDegrees * DegreesToRadians;
        if (cone != 0.0)
        {
            // Tilt basis taken from the mount frame rather than from the
            // pointing, so it turns with the mount.
            Vector3 towardPole = (-Math.Sin(declination) * meridian + Math.Cos(declination) * axis).Normalized();
            double phase = conePhaseDegrees * DegreesToRadians;
            Vector3 tilt = (Math.Cos(phase) * towardPole + Math.Sin(phase) * perpendicular).Normalized();
            atZero = (Math.Cos(cone) * atZero + Math.Sin(cone) * tilt).Normalized();
        }

        double cosRotation = Math.Cos(rotation);
        double sinRotation = Math.Sin(rotation);
        Vector3 pointing = (cosRotation * atZero
                            + sinRotation * axis.Cross(atZero)
                            + ((1.0 - cosRotation) * axis.Dot(atZero)) * axis).Normalized();

        return ToHorizontal(pointing);
    }

    /// <summary>
    /// The mount's own reference frame perpendicular to <paramref name="axis"/>:
    /// local vertical projected onto that plane is rotation zero, which is the
    /// mount's upper meridian. Falls back to north when the axis is vertical,
    /// where a meridian is undefined -- a site at the pole, which nothing in
    /// this project supports.
    /// </summary>
    private static (Vector3 Meridian, Vector3 Perpendicular) MountFrame(Vector3 axis)
    {
        var up = new Vector3(0, 0, 1);
        Vector3 projected = up - up.Dot(axis) * axis;

        if (projected.Length < 1e-9)
        {
            var north = new Vector3(1, 0, 0);
            projected = north - north.Dot(axis) * north;
        }

        Vector3 meridian = projected.Normalized();
        Vector3 perpendicular = axis.Cross(meridian).Normalized();
        return (meridian, perpendicular);
    }

    private static Vector3 ToVector(HorizontalCoordinates coordinates)
    {
        double altitude = coordinates.AltitudeDegrees * DegreesToRadians;
        double azimuth = coordinates.AzimuthDegrees * DegreesToRadians;
        double cosAltitude = Math.Cos(altitude);
        return new Vector3(cosAltitude * Math.Cos(azimuth), cosAltitude * Math.Sin(azimuth), Math.Sin(altitude));
    }

    private static HorizontalCoordinates ToHorizontal(Vector3 direction)
    {
        double azimuth = Math.Atan2(direction.Y, direction.X);
        if (azimuth < 0)
        {
            azimuth += 2.0 * Math.PI;
        }

        double altitude = Math.Atan2(direction.Z, Math.Sqrt(direction.X * direction.X + direction.Y * direction.Y));
        return new HorizontalCoordinates(azimuth * RadiansToDegrees, altitude * RadiansToDegrees);
    }
}
