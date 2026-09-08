using FreePolarAlign.Core.Astrometry;

namespace FreePolarAlign.Core.Alignment;

/// <summary>
/// Polar axis misalignment, as the two angles the altitude and azimuth bolts
/// control. Signed as "axis minus pole": positive altitude means the axis
/// points above the pole, positive azimuth means it points clockwise of the
/// pole seen from above (east of north in the northern hemisphere).
/// </summary>
public sealed record MountMisalignment(double AltitudeErrorArcminutes, double AzimuthErrorArcminutes)
{
    public static readonly MountMisalignment Aligned = new(0.0, 0.0);
}

/// <summary>
/// Generates the pointing directions a mount would actually produce, for
/// testing the fit against known ground truth. This is the forward direction of
/// the measurement the rest of the project runs backwards.
///
/// The sequence is built by placing the optical axis once, cone error included,
/// and then rotating it rigidly about the polar axis. That is not a
/// convenience: it is the physical claim the whole method rests on. Because the
/// optical axis is fixed in the rotating mount frame, its track is an exact
/// circle about the polar axis whatever the cone error, so cone error changes
/// only the circle's radius and never its axis -- which is why nothing in this
/// project models or calibrates it.
/// </summary>
public static class MountForwardModel
{
    private const double DegreesToRadians = Math.PI / 180.0;
    private const double ArcminutesToDegrees = 1.0 / 60.0;

    /// <summary>
    /// The visible celestial pole: north of the equator, south of it. Both poles
    /// sit on the horizon at the equator, where this returns the northern one by
    /// convention -- an equatorial site can align to either, and the caller has
    /// to say which.
    /// </summary>
    public static HorizontalCoordinates NominalPole(double siteLatitudeDegrees)
    {
        return siteLatitudeDegrees >= 0.0
            ? new HorizontalCoordinates(0.0, siteLatitudeDegrees)
            : new HorizontalCoordinates(180.0, -siteLatitudeDegrees);
    }

    /// <summary>Where the polar axis actually points, given a misalignment.</summary>
    public static HorizontalCoordinates MountAxis(double siteLatitudeDegrees, MountMisalignment misalignment)
    {
        HorizontalCoordinates pole = NominalPole(siteLatitudeDegrees);
        return new HorizontalCoordinates(
            pole.AzimuthDegrees + misalignment.AzimuthErrorArcminutes * ArcminutesToDegrees,
            pole.AltitudeDegrees + misalignment.AltitudeErrorArcminutes * ArcminutesToDegrees);
    }

    /// <param name="declinationDegrees">
    /// Declination the telescope is set to in the mount frame. Sets the circle's
    /// radius: 90 degrees minus this, before cone error.
    /// </param>
    /// <param name="coneErrorArcminutes">
    /// Angle between the optical axis and where it would sit with no cone error.
    /// </param>
    /// <param name="conePhaseDegrees">
    /// Direction of the cone offset within the mount frame. Varying it should
    /// change nothing about the recovered axis, only the circle radius.
    /// </param>
    /// <param name="rotationAnglesDegrees">
    /// Rotation about the polar axis for each capture, measured from the first.
    /// This is the RA sweep, and the fit's conditioning depends on its extent.
    /// </param>
    public static IReadOnlyList<HorizontalCoordinates> PointingSequence(
        double siteLatitudeDegrees,
        MountMisalignment misalignment,
        double declinationDegrees,
        double coneErrorArcminutes,
        double conePhaseDegrees,
        IReadOnlyList<double> rotationAnglesDegrees)
    {
        ArgumentNullException.ThrowIfNull(rotationAnglesDegrees);

        Vector3 axis = SmallCircleFitter.ToHorizonVector(MountAxis(siteLatitudeDegrees, misalignment));
        Vector3 opticalAxis = InitialOpticalAxis(axis, declinationDegrees, coneErrorArcminutes, conePhaseDegrees);

        var sequence = new HorizontalCoordinates[rotationAnglesDegrees.Count];
        for (int i = 0; i < rotationAnglesDegrees.Count; i++)
        {
            Vector3 rotated = RotateAbout(opticalAxis, axis, rotationAnglesDegrees[i] * DegreesToRadians);
            sequence[i] = SmallCircleFitter.ToHorizontalCoordinates(rotated);
        }

        return sequence;
    }

    /// <summary>
    /// Perturbs a direction by isotropic Gaussian noise of the given
    /// one-axis standard deviation, standing in for plate-solve error.
    /// </summary>
    public static HorizontalCoordinates AddNoise(HorizontalCoordinates direction, double sigmaArcseconds, Random random)
    {
        ArgumentNullException.ThrowIfNull(random);

        Vector3 v = SmallCircleFitter.ToHorizonVector(direction);
        var (eAlt, eAz) = OrthonormalComplement(v);

        double sigmaRadians = sigmaArcseconds * DegreesToRadians / 3600.0;
        double g1 = StandardNormal(random);
        double g2 = StandardNormal(random);

        Vector3 perturbed = (v + (sigmaRadians * g1) * eAlt + (sigmaRadians * g2) * eAz).Normalized();
        return SmallCircleFitter.ToHorizontalCoordinates(perturbed);
    }

    private static Vector3 InitialOpticalAxis(Vector3 axis, double declinationDegrees, double coneErrorArcminutes, double conePhaseDegrees)
    {
        var (meridian, perpendicular) = OrthonormalComplement(axis);

        double declination = declinationDegrees * DegreesToRadians;
        Vector3 ideal = (Math.Cos(declination) * meridian + Math.Sin(declination) * axis).Normalized();

        double cone = coneErrorArcminutes * ArcminutesToDegrees * DegreesToRadians;
        if (cone == 0.0)
        {
            return ideal;
        }

        // Tilt the optical axis by the cone angle in a fixed direction within
        // the mount frame. Exact rather than a small-angle offset, so that large
        // injected cone errors stay a genuine rotation.
        var (tiltA, tiltB) = OrthonormalComplement(ideal);
        double phase = conePhaseDegrees * DegreesToRadians;
        Vector3 tilt = (Math.Cos(phase) * tiltA + Math.Sin(phase) * tiltB).Normalized();

        return (Math.Cos(cone) * ideal + Math.Sin(cone) * tilt).Normalized();
    }

    private static Vector3 RotateAbout(Vector3 v, Vector3 axis, double angleRadians)
    {
        double c = Math.Cos(angleRadians);
        double s = Math.Sin(angleRadians);
        return (c * v + s * axis.Cross(v) + ((1.0 - c) * axis.Dot(v)) * axis).Normalized();
    }

    /// <summary>Any orthonormal pair spanning the plane perpendicular to <paramref name="v"/>.</summary>
    private static (Vector3 First, Vector3 Second) OrthonormalComplement(Vector3 v)
    {
        Vector3 seed = Math.Abs(v.Z) < 0.9 ? new Vector3(0, 0, 1) : new Vector3(1, 0, 0);
        Vector3 first = (seed - seed.Dot(v) * v).Normalized();
        Vector3 second = v.Cross(first).Normalized();
        return (first, second);
    }

    private static double StandardNormal(Random random)
    {
        double u1 = 1.0 - random.NextDouble();
        double u2 = random.NextDouble();
        return Math.Sqrt(-2.0 * Math.Log(u1)) * Math.Cos(2.0 * Math.PI * u2);
    }
}
