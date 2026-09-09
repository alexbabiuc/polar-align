using FreePolarAlign.Core.Alignment;
using FreePolarAlign.Core.Astrometry;
using FreePolarAlign.Devices;

namespace FreePolarAlign.Session;

/// <summary>
/// One capture, as a mechanical target rather than a sky position.
/// </summary>
/// <param name="MechanicalRotationDegrees">
/// Hour angle in the mount's own frame. Its *sign* is the meridian side, which
/// is what D8 constrains, so the plan is expressed in this coordinate rather
/// than in right ascension.
/// </param>
public sealed record PlannedCapture(
    double MechanicalRotationDegrees,
    double PredictedAltitudeDegrees);

/// <param name="MechanicalDeclinationDegrees">
/// The single declination the whole sequence holds, in the mount's own frame.
/// Constant by construction, which is the property the method depends on (D11).
/// </param>
/// <param name="Reason">Why no plan could be made, phrased for the user.</param>
public sealed record TargetPlan(
    IReadOnlyList<PlannedCapture> Captures,
    double MechanicalDeclinationDegrees,
    bool IsWest,
    string? Reason)
{
    public bool Success => Captures.Count > 0;
}

/// <summary>
/// Chooses where to point and how to sweep.
///
/// The plan is built in the mount's mechanical coordinates rather than in right
/// ascension, because that is where the constraint that matters actually lives:
/// D8 forbids crossing the meridian during a sequence, and the meridian is
/// exactly the sign change of the mechanical hour angle. Planning in hour angle
/// makes "stay on one side" a property of the plan by construction instead of
/// something to be checked afterwards and hoped for.
/// </summary>
public static class TargetSelection
{
    /// <summary>
    /// Minimum altitude for a capture. Below roughly thirty degrees refraction
    /// grows quickly and seeing worsens, both of which degrade the solve; the
    /// sky is large enough that there is no reason to accept it.
    /// </summary>
    public const double MinimumAltitudeDegrees = 30.0;

    /// <summary>
    /// Closest approach to the pole. Phase 1 established that the circle's
    /// radius does not affect the fit's conditioning, so this is not about
    /// precision -- it is about staying far away from the radius at which the
    /// arc stops being distinguishable from a straight line and the axis
    /// becomes unidentifiable (D11).
    /// </summary>
    public const double MinimumPoleDistanceDegrees = 20.0;

    /// <summary>
    /// Margin either side of the meridian left unused, so that sidereal
    /// tracking during the sequence cannot carry a capture across it (D8).
    /// </summary>
    public const double MeridianMarginDegrees = 5.0;

    /// <param name="sweepDegrees">
    /// Total extent of the sweep. Phase 1 measured axis uncertainty falling as
    /// the *square* of this, against only the square root of the capture count,
    /// so this is the parameter worth spending on -- bounded by D8's meridian
    /// limit rather than by anything else.
    /// </param>
    public static TargetPlan Plan(
        GeodeticLocation site,
        DateTime startUtc,
        int captureCount,
        double sweepDegrees = 70.0,
        AtmosphericConditions? atmosphere = null)
    {
        ArgumentNullException.ThrowIfNull(site);
        if (captureCount < SmallCircleFitter.MinimumObservations)
        {
            return new TargetPlan(
                Array.Empty<PlannedCapture>(), 0.0, false,
                $"A sequence needs at least {SmallCircleFitter.MinimumObservations} captures (D7).");
        }

        atmosphere ??= AtmosphericConditions.Vacuum;
        var observer = new ObserverSite(site.LatitudeDegrees, site.LongitudeDegrees, site.HeightMeters);
        double latitude = site.LatitudeDegrees;

        // Declinations are tried from the pole outward but never nearer than the
        // pole-distance floor; on the same side of the equator as the observer,
        // since that is where a target stays high.
        var candidateDeclinations = new List<double>();
        for (double poleDistance = MinimumPoleDistanceDegrees; poleDistance <= 75.0; poleDistance += 5.0)
        {
            candidateDeclinations.Add(latitude >= 0 ? 90.0 - poleDistance : -90.0 + poleDistance);
        }

        foreach (bool west in new[] { true, false })
        {
            foreach (double declination in candidateDeclinations)
            {
                TargetPlan plan = TryPlan(observer, site, startUtc, captureCount, sweepDegrees, declination, west, atmosphere);
                if (plan.Success)
                {
                    return plan;
                }
            }
        }

        return new TargetPlan(
            Array.Empty<PlannedCapture>(), 0.0, false,
            $"No target keeps all {captureCount} captures above {MinimumAltitudeDegrees}° on one side of the meridian " +
            $"with a {sweepDegrees}° sweep. Wait for the sky to rotate, or reduce the sweep.");
    }

    private static TargetPlan TryPlan(
        ObserverSite observer,
        GeodeticLocation site,
        DateTime startUtc,
        int captureCount,
        double sweepDegrees,
        double declinationDegrees,
        bool west,
        AtmosphericConditions atmosphere)
    {
        // Entirely one side of the meridian, margin included, so tracking during
        // the sequence cannot carry a capture across it.
        double nearEdge = MeridianMarginDegrees;
        double farEdge = MeridianMarginDegrees + sweepDegrees;
        double sign = west ? 1.0 : -1.0;

        var captures = new List<PlannedCapture>(captureCount);
        for (int i = 0; i < captureCount; i++)
        {
            double fraction = captureCount == 1 ? 0.0 : i / (double)(captureCount - 1);
            double rotation = sign * (nearEdge + fraction * (farEdge - nearEdge));

            HorizontalCoordinates pointing = MountMechanics.Compose(
                site.LatitudeDegrees, MountMisalignment.Aligned, rotation, declinationDegrees);

            if (pointing.AltitudeDegrees < MinimumAltitudeDegrees)
            {
                return new TargetPlan(Array.Empty<PlannedCapture>(), declinationDegrees, west, "too low");
            }

            captures.Add(new PlannedCapture(rotation, pointing.AltitudeDegrees));
        }

        return new TargetPlan(captures, declinationDegrees, west, null);
    }

    /// <summary>
    /// Turns a mechanical target into the sky coordinates to command, for the
    /// instant the slew will actually happen.
    ///
    /// Resolved per capture rather than once at planning time, and that is the
    /// crux of keeping the sequence valid. What must stay constant is the
    /// *mechanical* declination, since that is what fixes the optical axis in
    /// the rotating frame (D11). A constant mechanical declination is a constant
    /// declination about the pole *of date*, so its J2000 equivalent drifts with
    /// right ascension by a few arcminutes across a wide sweep -- measured, 5.75
    /// arcminutes over 70 degrees. Commanding a fixed J2000 declination
    /// therefore makes the mount move in declination, and commanding coordinates
    /// computed for a stale instant does the same.
    ///
    /// Resolving at the moment of the slew makes the round trip exact: the mount
    /// decomposes what it is given and recovers precisely the mechanical angles
    /// asked for.
    ///
    /// The conversion is deliberately geometric, with no refraction, because a
    /// mount's pointing model is geometric; this has to be the exact inverse of
    /// what the mount will do to the coordinates, not the most physically
    /// complete transform available.
    /// </summary>
    public static (double RaDegrees, double DecDegrees) ResolveCommand(
        GeodeticLocation site,
        double mechanicalRotationDegrees,
        double mechanicalDeclinationDegrees,
        DateTime utc)
    {
        HorizontalCoordinates ideal = MountMechanics.Compose(
            site.LatitudeDegrees, MountMisalignment.Aligned, mechanicalRotationDegrees, mechanicalDeclinationDegrees);

        var observer = new ObserverSite(site.LatitudeDegrees, site.LongitudeDegrees, site.HeightMeters);
        return TopocentricConverter.FromAltAz(ideal, utc, observer, AtmosphericConditions.Vacuum);
    }

    /// <summary>
    /// Hour angle of a commanded position, computed independently of anything
    /// the mount reports.
    ///
    /// This is the cross-check D8 and D9 both call for: some drivers report
    /// <c>SideOfPier</c> unreliably, and an undetected meridian flip mid-sequence
    /// inverts the sense of cone error and silently invalidates the fit. Deriving
    /// the hour angle from the commanded coordinates and the clock depends on no
    /// driver at all.
    /// </summary>
    public static double MechanicalRotationOf(
        GeodeticLocation site,
        double raDegrees,
        double decDegrees,
        DateTime utc)
    {
        var observer = new ObserverSite(site.LatitudeDegrees, site.LongitudeDegrees, site.HeightMeters);
        HorizontalCoordinates pointing = TopocentricConverter.ToAltAz(
            raDegrees, decDegrees, utc, observer, AtmosphericConditions.Vacuum);

        var (rotation, _) = MountMechanics.Decompose(site.LatitudeDegrees, pointing);
        return rotation;
    }

    private static double Wrap360(double degrees)
    {
        double wrapped = degrees % 360.0;
        return wrapped < 0 ? wrapped + 360.0 : wrapped;
    }
}
