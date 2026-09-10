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
/// <param name="SweepDegrees">
/// The sweep actually planned, which can be less than requested. The planner
/// shrinks rather than place a capture below the altitude floor or across the
/// meridian, and since axis uncertainty scales as the inverse *square* of the
/// sweep, a shrunk plan is a materially worse measurement -- so the achieved
/// figure is carried out to the UI rather than left as an internal detail.
/// </param>
/// <param name="AnchoredAtCurrentPointing">
/// True when the first capture is where the telescope already points, so the
/// sequence can begin without moving anything (D18). False when the current
/// position was unusable and a fresh target was chosen instead.
/// </param>
public sealed record TargetPlan(
    IReadOnlyList<PlannedCapture> Captures,
    double MechanicalDeclinationDegrees,
    bool IsWest,
    string? Reason,
    double SweepDegrees = 0.0,
    bool AnchoredAtCurrentPointing = false)
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

    /// <summary>
    /// The least sweep worth attempting. Phase 1 measured axis uncertainty
    /// falling as the square of the sweep, so thirty degrees is already about
    /// five times worse than seventy; below that the measurement stops being
    /// good enough to be worth the user's twenty minutes, and the fit's own
    /// curvature-identifiability guard (D11) starts refusing the answer anyway.
    /// </summary>
    public const double MinimumUsefulSweepDegrees = 30.0;

    /// <summary>
    /// Hard ceiling on the sweep. One side of the meridian is 180 degrees of
    /// hour angle less the margin at each end, and nothing near that is
    /// reachable above the altitude floor -- but the cap keeps a mistyped
    /// request from generating a plan that wraps past the pole.
    /// </summary>
    public const double MaximumSweepDegrees = 150.0;

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
        // pole-distance floor.
        //
        // Mechanical declination is measured from the mount's equator towards
        // the pole its axis actually points at, which is the *visible* pole --
        // so it is positive towards that pole in both hemispheres, and is not
        // the same quantity as sky declination south of the equator. Negating it
        // for a southern site (as though it were sky declination) picks targets
        // on the far side of the mount's equator: measured, latitude -33.9° then
        // yielded a target 105° from the polar axis, only six degrees above the
        // horizon, and quietly settled for a 30° sweep instead of the 70°
        // requested. It "succeeded", which is what made it worth a comment.
        var candidateDeclinations = new List<double>();
        for (double poleDistance = MinimumPoleDistanceDegrees; poleDistance <= 75.0; poleDistance += 5.0)
        {
            candidateDeclinations.Add(90.0 - poleDistance);
        }

        // Largest sweep first, since uncertainty falls as its square; only then
        // is the sweep given up degree by degree.
        foreach (double sweep in ShrinkingSweeps(sweepDegrees))
        {
            foreach (bool west in new[] { true, false })
            {
                foreach (double declination in candidateDeclinations)
                {
                    TargetPlan plan = TryPlan(observer, site, startUtc, captureCount, sweep, declination, west, atmosphere);
                    if (plan.Success)
                    {
                        return plan;
                    }
                }
            }
        }

        return new TargetPlan(
            Array.Empty<PlannedCapture>(), 0.0, false,
            $"No target keeps all {captureCount} captures above {MinimumAltitudeDegrees}° on one side of the meridian, " +
            $"even with the sweep reduced to {MinimumUsefulSweepDegrees}°. Wait for the sky to rotate.");
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

        return new TargetPlan(captures, declinationDegrees, west, null, sweepDegrees, AnchoredAtCurrentPointing: false);
    }

    /// <summary>
    /// Plans a sequence that starts where the telescope is already pointing.
    ///
    /// This is what D18 needs: nothing turns until the user says so, and the
    /// cheapest first capture is the one that requires no motion at all. It also
    /// keeps the sequence in whatever part of the sky the user already chose --
    /// which they can see and the software cannot.
    ///
    /// The anchor is taken from the mount's *own* mechanical angles rather than
    /// from a solved position, because what has to stay constant across the
    /// sequence is the mechanical declination (D11), and that is a property of
    /// the frame the mount drives in. A solved position includes the
    /// misalignment and any cone error, so decomposing it would anchor the plan
    /// to a declination the mount cannot hold.
    /// </summary>
    /// <returns>
    /// A plan whose first capture is the current position when that position is
    /// usable, and otherwise a plan for a freshly chosen target with
    /// <see cref="TargetPlan.AnchoredAtCurrentPointing"/> false -- in which case
    /// the caller must propose a slew before the first capture.
    /// </returns>
    public static TargetPlan PlanFrom(
        GeodeticLocation site,
        double currentMechanicalRotationDegrees,
        double currentMechanicalDeclinationDegrees,
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

        TargetPlan anchored = TryPlanFromCurrent(
            site, currentMechanicalRotationDegrees, currentMechanicalDeclinationDegrees, captureCount, sweepDegrees);

        if (anchored.Success)
        {
            return anchored;
        }

        // The current pointing cannot carry a sequence -- too near the pole, too
        // low, or too near the meridian to sweep away from it. Choosing a fresh
        // target is better than refusing, but the caller has to be told that the
        // first capture now needs a slew, which is what the flag is for.
        TargetPlan fresh = Plan(site, startUtc, captureCount, sweepDegrees, atmosphere);
        return fresh.Success
            ? fresh with { Reason = anchored.Reason }
            : fresh;
    }

    private static TargetPlan TryPlanFromCurrent(
        GeodeticLocation site,
        double currentRotationDegrees,
        double currentDeclinationDegrees,
        int captureCount,
        double requestedSweepDegrees)
    {
        // Distance from the pole the mount actually rotates about. Mechanical
        // declination is positive towards that pole in either hemisphere (see
        // Plan), so this is the same expression north and south.
        //
        // Near the pole the arc stops being distinguishable from a straight line
        // and the axis becomes unidentifiable (D11), which the fit's own
        // curvature guard would catch -- but catching it here means saying so
        // before the user spends twenty minutes capturing. Beyond ninety degrees
        // the telescope is past the mount's equator, on its way to the opposite
        // pole, which is below the horizon.
        double poleDistance = 90.0 - currentDeclinationDegrees;
        if (poleDistance < MinimumPoleDistanceDegrees)
        {
            return new TargetPlan(
                Array.Empty<PlannedCapture>(), currentDeclinationDegrees, currentRotationDegrees >= 0.0,
                $"The telescope is {poleDistance:F0}° from the pole, closer than the {MinimumPoleDistanceDegrees:F0}° " +
                "needed for the arc to curve measurably (D11).");
        }

        if (poleDistance > 90.0)
        {
            return new TargetPlan(
                Array.Empty<PlannedCapture>(), currentDeclinationDegrees, currentRotationDegrees >= 0.0,
                $"The telescope is {poleDistance:F0}° from the pole the mount turns about, so it is pointing past " +
                "the mount's equator towards the opposite pole, which never rises here.");
        }

        if (Math.Abs(currentRotationDegrees) < MeridianMarginDegrees)
        {
            return new TargetPlan(
                Array.Empty<PlannedCapture>(), currentDeclinationDegrees, currentRotationDegrees >= 0.0,
                $"The telescope is within {MeridianMarginDegrees:F0}° of the meridian, so a sweep from here would " +
                "cross it and invert the sense of cone error mid-sequence (D8).");
        }

        double side = currentRotationDegrees >= 0.0 ? 1.0 : -1.0;
        TargetPlan? best = null;

        foreach (double sweep in ShrinkingSweeps(requestedSweepDegrees))
        {
            // Away from the meridian, or back towards it: both stay on one side,
            // and which one keeps the target higher depends entirely on where
            // the mount happens to be sitting.
            foreach (double direction in new[] { 1.0, -1.0 })
            {
                TargetPlan candidate = BuildSweep(
                    site, currentRotationDegrees, currentDeclinationDegrees, captureCount, sweep, side, direction);

                if (!candidate.Success)
                {
                    continue;
                }

                if (best is null || MinimumAltitudeOf(candidate) > MinimumAltitudeOf(best))
                {
                    best = candidate;
                }
            }

            // Sweep is worth more than altitude margin, so the first sweep that
            // works at all is taken, and the direction choice happens within it.
            if (best is not null)
            {
                return best;
            }
        }

        return new TargetPlan(
            Array.Empty<PlannedCapture>(), currentDeclinationDegrees, side > 0.0,
            $"No sweep of at least {MinimumUsefulSweepDegrees:F0}° from the current position keeps every capture " +
            $"above {MinimumAltitudeDegrees:F0}° on this side of the meridian.");
    }

    private static TargetPlan BuildSweep(
        GeodeticLocation site,
        double anchorRotationDegrees,
        double declinationDegrees,
        int captureCount,
        double sweepDegrees,
        double side,
        double direction)
    {
        var captures = new List<PlannedCapture>(captureCount);

        for (int i = 0; i < captureCount; i++)
        {
            double fraction = i / (double)(captureCount - 1);
            double rotation = anchorRotationDegrees + direction * side * fraction * sweepDegrees;

            // Same side of the meridian throughout, margin included, so tracking
            // during the sequence cannot carry a capture across it (D8).
            if (Math.Sign(rotation) != Math.Sign(side) || Math.Abs(rotation) < MeridianMarginDegrees)
            {
                return new TargetPlan(Array.Empty<PlannedCapture>(), declinationDegrees, side > 0.0, "crosses the meridian");
            }

            HorizontalCoordinates pointing = MountMechanics.Compose(
                site.LatitudeDegrees, MountMisalignment.Aligned, rotation, declinationDegrees);

            if (pointing.AltitudeDegrees < MinimumAltitudeDegrees)
            {
                return new TargetPlan(Array.Empty<PlannedCapture>(), declinationDegrees, side > 0.0, "too low");
            }

            captures.Add(new PlannedCapture(rotation, pointing.AltitudeDegrees));
        }

        return new TargetPlan(
            captures, declinationDegrees, side > 0.0, null, sweepDegrees, AnchoredAtCurrentPointing: true);
    }

    private static double MinimumAltitudeOf(TargetPlan plan) =>
        plan.Captures.Min(c => c.PredictedAltitudeDegrees);

    /// <summary>
    /// Sweeps to try, largest first, down to the floor. Five-degree steps: finer
    /// would be false precision given that the altitude floor is itself a
    /// judgement, and coarser would give up usable sweep unnecessarily.
    /// </summary>
    private static IEnumerable<double> ShrinkingSweeps(double requested)
    {
        double sweep = Math.Min(requested, MaximumSweepDegrees);
        while (sweep >= MinimumUsefulSweepDegrees)
        {
            yield return sweep;
            sweep -= 5.0;
        }
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
