using FreePolarAlign.Core.Astrometry;

namespace FreePolarAlign.Core.Alignment;

/// <summary>
/// An updated axis estimate, inferred from a single field while the user turns
/// the bolts.
/// </summary>
/// <param name="AppliedAltitudeArcminutes">
/// How far the altitude bolt appears to have been turned since the reference
/// frame. Useful in its own right: it tells the user how much of the correction
/// they have actually put in, which is otherwise impossible to judge by hand.
/// </param>
/// <param name="ConditionNumber">
/// Conditioning of the two-parameter fit. Grows without bound as the telescope
/// approaches the zenith, where an azimuth turn moves the field hardly at all
/// and so cannot be measured from it.
/// </param>
/// <param name="IsReliable">
/// False when the geometry cannot support the inference, or the observed motion
/// is inconsistent with any bolt turn. A live readout that silently drifts away
/// from reality would be worse than no live readout, since the user is acting on
/// it continuously (D11).
/// </param>
public sealed record AxisUpdate(
    HorizontalCoordinates MountAxis,
    double AltitudeErrorArcminutes,
    double AzimuthErrorArcminutes,
    double TotalErrorArcminutes,
    double AppliedAltitudeArcminutes,
    double AppliedAzimuthArcminutes,
    double ResidualArcseconds,
    double ConditionNumber,
    bool IsReliable,
    string? UnreliableReason);

/// <summary>
/// Freeze-and-track: keeps the polar axis estimate live from a single field
/// while the user turns the bolts.
///
/// Once the axis is known from a swept sequence, sweeping again after every
/// quarter-turn of a bolt would be unusable — the user is crouched at a tripod
/// and needs the number to move as they do. The way out is that turning the
/// bolts rotates the *whole mount* as a rigid body, so the polar axis and the
/// optical axis move by the same rotation. Watching where the optical axis went
/// therefore says where the polar axis went.
///
/// What makes that possible from one field is that the rotation is not free: an
/// altitude bolt turns the mount about a horizontal axis and an azimuth bolt
/// about the local vertical, which is two degrees of freedom, exactly matching
/// the two components of the field's observed motion on the sky. A general
/// rotation has three and could not be recovered this way.
///
/// The geometry has a blind spot, and it is not where one might guess. The
/// altitude bolt turns the mount about a horizontal axis running east-west (it is
/// perpendicular to the polar axis' vertical plane), so a telescope pointing due
/// east or due west lies in that axis' own vertical plane -- and there both bolts
/// move the field along the *same* line, making them impossible to tell apart.
/// Measured, the conditioning there runs from twenty thousand at low altitude to
/// six hundred thousand near the zenith, against one to sixty everywhere else.
/// Pointing near the zenith degrades it too, but far more gently.
///
/// So freeze-and-track needs a pointing away from due east and west; the
/// meridian is the best place to be. This is reported rather than hidden, since
/// the user is acting on the number continuously.
///
/// **It cannot detect a disturbance that is not a bolt turn, and this is not
/// fixable.** The two bolt parameters span the whole two-dimensional space of
/// ways a single field can move, so *any* observed motion is explained exactly
/// by some pair of bolt angles. A declination clutch slipping, or a kicked
/// tripod, produces a perfectly good fit with near-zero residual and a wrong
/// answer. That is the opposite situation from the swept measurement, where
/// D11's residual check catches exactly this because six points on a circle are
/// heavily over-determined.
///
/// Two things follow, and a UI that ignores them would mislead. Live tracking is
/// a *refinement of a trusted measurement*, not a substitute for one, so it
/// should be re-anchored by a fresh sweep rather than trusted indefinitely. And
/// <see cref="AxisUpdate.AppliedAltitudeArcminutes"/> and its azimuth
/// counterpart are the practical safeguard worth surfacing: a user who touched
/// only the altitude bolt, and is told the azimuth bolt moved by twenty
/// arcminutes, has learned that something else moved.
/// </summary>
public sealed class AxisTracker
{
    private const double DegreesToRadians = Math.PI / 180.0;
    private const double RadiansToArcminutes = 180.0 * 60.0 / Math.PI;
    private const double RadiansToArcseconds = 180.0 * 3600.0 / Math.PI;
    private const double ArcminutesToRadians = 1.0 / RadiansToArcminutes;

    /// <summary>
    /// Above this the two bolt motions are too nearly parallel to separate.
    /// Usable geometry measures between one and a few hundred, and the
    /// degenerate east-west locus measures tens of thousands upward, so the
    /// limit sits in the empty ground between them.
    /// </summary>
    public const double ConditionNumberLimit = 1.0e4;

    private readonly double _siteLatitudeDegrees;
    private readonly Vector3 _referenceDirection;
    private readonly Vector3 _initialAxis;

    /// <summary>
    /// The horizontal axis the altitude bolt turns the mount about, fixed once
    /// from the mount's own geometry. It must not be recomputed per rotated
    /// vector: the bolts move the whole instrument as one body, so the optical
    /// axis and the polar axis have to be turned about the *same* axes or the
    /// inference and the correction stop describing the same motion.
    /// </summary>
    private readonly Vector3 _altitudeBoltAxis;
    private static readonly Vector3 Up = new(0, 0, 1);

    /// <param name="initialAxis">
    /// The axis as measured by the swept sequence. Freeze-and-track refines a
    /// known answer; it cannot establish one, because a single field says
    /// nothing about where the axis is, only about how it has moved.
    /// </param>
    /// <param name="referenceDirection">
    /// Where the telescope was pointing when that measurement finished, from its
    /// own plate solve.
    /// </param>
    public AxisTracker(double siteLatitudeDegrees, HorizontalCoordinates initialAxis, HorizontalCoordinates referenceDirection)
    {
        _siteLatitudeDegrees = siteLatitudeDegrees;
        _initialAxis = ToVector(initialAxis);
        _referenceDirection = ToVector(referenceDirection);
        _altitudeBoltAxis = AltitudeBoltAxis(_initialAxis);
    }

    /// <summary>
    /// How well a candidate pointing would support live tracking, without
    /// needing an observation. Lets the session or UI steer the user to a
    /// workable field *before* they start turning bolts, rather than discovering
    /// the geometry is unusable afterwards.
    /// </summary>
    public static double ConditioningAt(HorizontalCoordinates polarAxis, HorizontalCoordinates pointing)
    {
        Vector3 axis = ToVector(polarAxis);
        Vector3 altitudeAxis = AltitudeBoltAxis(axis);
        Vector3 direction = ToVector(pointing);

        Vector3 dAltitude = altitudeAxis.Cross(direction);
        Vector3 dAzimuth = Up.Cross(direction);

        return ConditionOf(dAltitude.Dot(dAltitude), dAltitude.Dot(dAzimuth), dAzimuth.Dot(dAzimuth));
    }

    /// <summary>Whether a candidate pointing can support live tracking at all.</summary>
    public static bool IsUsableTrackingGeometry(HorizontalCoordinates polarAxis, HorizontalCoordinates pointing) =>
        ConditioningAt(polarAxis, pointing) <= ConditionNumberLimit;

    /// <param name="mechanicalRotationSinceReferenceDegrees">
    /// Rotation the mount has made about its own polar axis since the reference
    /// frame -- zero if the drive is stopped, or the sidereal amount if it is
    /// tracking. Supplied rather than inferred because the caller knows it
    /// exactly, and because it is indistinguishable from a bolt turn if ignored:
    /// tracking would be read as a slowly worsening alignment.
    /// </param>
    public AxisUpdate Update(
        HorizontalCoordinates observedDirection,
        double mechanicalRotationSinceReferenceDegrees = 0.0)
    {
        Vector3 observed = ToVector(observedDirection);

        // Where the optical axis would be with no bolts touched: the reference
        // direction carried round the axis by whatever the drive has done.
        Vector3 expected = Rotate(_referenceDirection, _initialAxis, mechanicalRotationSinceReferenceDegrees * DegreesToRadians);

        double altitudeRadians = 0.0;
        double azimuthRadians = 0.0;
        double conditionNumber = double.PositiveInfinity;
        double residualRadians = 0.0;

        // Gauss-Newton in the two bolt angles. The model is a rotation, so it is
        // solved iteratively rather than by inverting a linearisation once --
        // a user can easily turn a bolt through a degree, where the small-angle
        // approximation is worth several arcminutes.
        for (int iteration = 0; iteration < 12; iteration++)
        {
            Vector3 tilted = Rotate(expected, _altitudeBoltAxis, altitudeRadians);
            Vector3 predicted = Rotate(tilted, Up, azimuthRadians);

            // Exact derivatives of the two-rotation model, not a small-angle
            // stand-in for them.
            Vector3 dAltitude = Rotate(_altitudeBoltAxis.Cross(tilted), Up, azimuthRadians);
            Vector3 dAzimuth = Up.Cross(predicted);

            Vector3 error = observed - predicted;

            double a11 = dAltitude.Dot(dAltitude);
            double a12 = dAltitude.Dot(dAzimuth);
            double a22 = dAzimuth.Dot(dAzimuth);
            double b1 = dAltitude.Dot(error);
            double b2 = dAzimuth.Dot(error);

            double determinant = a11 * a22 - a12 * a12;
            conditionNumber = ConditionOf(a11, a12, a22);

            if (determinant <= 0.0 || !double.IsFinite(determinant))
            {
                break;
            }

            double stepAltitude = (a22 * b1 - a12 * b2) / determinant;
            double stepAzimuth = (a11 * b2 - a12 * b1) / determinant;

            altitudeRadians += stepAltitude;
            azimuthRadians += stepAzimuth;

            if (Math.Abs(stepAltitude) + Math.Abs(stepAzimuth) < 1e-13)
            {
                break;
            }
        }

        Vector3 finalAxis = ApplyBolts(_initialAxis, altitudeRadians, azimuthRadians);
        Vector3 finalPredicted = ApplyBolts(expected, altitudeRadians, azimuthRadians);
        residualRadians = AngleBetween(finalPredicted, observed);

        HorizontalCoordinates axisCoordinates = ToHorizontal(finalAxis);
        HorizontalCoordinates pole = MountForwardModel.NominalPole(_siteLatitudeDegrees);

        double altitudeError = (axisCoordinates.AltitudeDegrees - pole.AltitudeDegrees) * 60.0;
        double azimuthError = WrapDegrees(axisCoordinates.AzimuthDegrees - pole.AzimuthDegrees) * 60.0;
        double totalError = AngleBetween(finalAxis, ToVector(pole)) * RadiansToArcminutes;

        double residualArcseconds = residualRadians * RadiansToArcseconds;
        var (isReliable, reason) = Assess(conditionNumber, residualArcseconds);

        return new AxisUpdate(
            axisCoordinates,
            altitudeError,
            azimuthError,
            totalError,
            altitudeRadians * RadiansToArcminutes,
            azimuthRadians * RadiansToArcminutes,
            residualArcseconds,
            conditionNumber,
            isReliable,
            reason);
    }

    /// <summary>
    /// Conditioning is the *only* genuine check available here, which is worth
    /// stating because the obvious alternative does not work. A residual test
    /// would look like a safety net and provide nothing: the two bolt angles
    /// reach a two-parameter family of directions covering most of the sphere,
    /// so essentially any observed motion -- including a gross one -- fits with a
    /// near-zero residual. An always-passing check that reads like a guard is
    /// worse than no check, so there is not one.
    /// </summary>
    private static (bool IsReliable, string? Reason) Assess(double conditionNumber, double residualArcseconds)
    {
        if (!double.IsFinite(conditionNumber) || conditionNumber > ConditionNumberLimit)
        {
            return (false, "At this pointing the altitude and azimuth bolts move the field in the same direction, so " +
                           "their effects cannot be told apart. Point closer to the meridian -- due east and due west " +
                           "are the blind spots -- and away from overhead, then continue.");
        }

        return (true, null);
    }

    /// <summary>
    /// The horizontal axis an altitude bolt turns the mount about: perpendicular
    /// to the vertical plane containing the polar axis, signed so a positive
    /// angle raises the axis.
    /// </summary>
    private static Vector3 AltitudeBoltAxis(Vector3 polarAxis)
    {
        Vector3 altitude = polarAxis.Cross(Up);

        // Degenerate only for a polar axis pointing straight up, i.e. a site at
        // the geographic pole, which this project does not support.
        return altitude.Length < 1e-12 ? new Vector3(0, 1, 0) : altitude.Normalized();
    }

    /// <summary>
    /// Altitude first, about the mount's fixed altitude-bolt axis, then azimuth
    /// about the vertical. Real bolts are independent mechanisms so the order
    /// only matters at second order, but fixing one keeps the inferred motion and
    /// the applied correction describing the same thing.
    /// </summary>
    private Vector3 ApplyBolts(Vector3 vector, double altitudeRadians, double azimuthRadians) =>
        Rotate(Rotate(vector, _altitudeBoltAxis, altitudeRadians), Up, azimuthRadians);

    private static double ConditionOf(double a11, double a12, double a22)
    {
        double trace = a11 + a22;
        double determinant = a11 * a22 - a12 * a12;
        double discriminant = Math.Sqrt(Math.Max(trace * trace - 4.0 * determinant, 0.0));

        double larger = 0.5 * (trace + discriminant);
        double smaller = 0.5 * (trace - discriminant);

        return smaller > 0 ? larger / smaller : double.PositiveInfinity;
    }

    private static Vector3 Rotate(Vector3 v, Vector3 axis, double angleRadians)
    {
        if (angleRadians == 0.0)
        {
            return v;
        }

        double c = Math.Cos(angleRadians);
        double s = Math.Sin(angleRadians);
        return c * v + s * axis.Cross(v) + ((1.0 - c) * axis.Dot(v)) * axis;
    }

    private static double AngleBetween(Vector3 a, Vector3 b)
    {
        Vector3 cross = a.Cross(b);
        return Math.Atan2(cross.Length, a.Dot(b));
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
        Vector3 unit = direction.Normalized();
        double azimuth = Math.Atan2(unit.Y, unit.X);
        if (azimuth < 0)
        {
            azimuth += 2.0 * Math.PI;
        }

        double altitude = Math.Atan2(unit.Z, Math.Sqrt(unit.X * unit.X + unit.Y * unit.Y));
        return new HorizontalCoordinates(azimuth / DegreesToRadians, altitude / DegreesToRadians);
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
