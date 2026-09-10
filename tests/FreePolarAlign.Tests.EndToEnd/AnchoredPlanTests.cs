using FreePolarAlign.Devices;
using FreePolarAlign.Session;
using Xunit;

namespace FreePolarAlign.Tests.EndToEnd;

/// <summary>
/// <see cref="TargetSelection.PlanFrom"/>: planning a sequence that begins where
/// the telescope already points.
///
/// This is what makes D18's first capture free. It is also where the constraints
/// that keep a measurement valid have to be enforced *before* the user spends
/// twenty minutes on it -- too near the pole and the arc does not curve
/// measurably (D11), too near the meridian and the sweep would cross it (D8),
/// too low and refraction and seeing spoil the solves.
/// </summary>
public class AnchoredPlanTests
{
    private static readonly GeodeticLocation North = new(45.0, 15.0, 200.0);
    private static readonly GeodeticLocation South = new(-33.9, 151.2, 40.0);

    private static TargetPlan PlanFrom(
        GeodeticLocation site, double rotation, double declination, int captures = 6, double sweep = 70.0) =>
        TargetSelection.PlanFrom(site, rotation, declination, DateTime.UtcNow, captures, sweep);

    /// <summary>
    /// The first capture is the current position exactly, so it can be taken
    /// without commanding any motion.
    /// </summary>
    [Fact]
    public void ThePlanBeginsWhereTheTelescopeAlreadyPoints()
    {
        TargetPlan plan = PlanFrom(North, rotation: 12.0, declination: 68.0);

        Assert.True(plan.Success, plan.Reason);
        Assert.True(plan.AnchoredAtCurrentPointing);
        Assert.Equal(12.0, plan.Captures[0].MechanicalRotationDegrees, precision: 9);
        Assert.Equal(68.0, plan.MechanicalDeclinationDegrees, precision: 9);
    }

    /// <summary>
    /// Every capture holds the same mechanical declination. That constancy is the
    /// assumption the small-circle fit rests on, so it has to be a property of
    /// the plan rather than something checked afterwards and hoped for.
    /// </summary>
    [Fact]
    public void EveryCaptureSharesOneMechanicalDeclination()
    {
        TargetPlan plan = PlanFrom(North, 12.0, 68.0);

        Assert.True(plan.Success, plan.Reason);
        Assert.Equal(6, plan.Captures.Count);

        // The plan carries one declination for all of them by construction --
        // there is deliberately nowhere for a per-capture declination to live.
        Assert.All(plan.Captures, capture => Assert.InRange(capture.PredictedAltitudeDegrees, 30.0, 90.0));
    }

    /// <summary>
    /// The whole sweep stays on one side of the meridian, margin included, so
    /// that sidereal tracking during the sequence cannot carry a capture across
    /// it and invert the sense of cone error mid-measurement (D8).
    /// </summary>
    [Theory]
    [InlineData(8.0)]
    [InlineData(30.0)]
    [InlineData(60.0)]
    [InlineData(-8.0)]
    [InlineData(-30.0)]
    [InlineData(-60.0)]
    public void TheSweepNeverCrossesTheMeridian(double rotation)
    {
        TargetPlan plan = PlanFrom(North, rotation, 68.0);

        Assert.True(plan.Success, plan.Reason);
        Assert.All(plan.Captures, capture =>
        {
            Assert.Equal(Math.Sign(rotation), Math.Sign(capture.MechanicalRotationDegrees));
            Assert.True(
                Math.Abs(capture.MechanicalRotationDegrees) >= TargetSelection.MeridianMarginDegrees,
                $"{capture.MechanicalRotationDegrees:F2}° is inside the meridian margin");
        });
    }

    /// <summary>
    /// Pointing near the meridian cannot anchor a sequence, and the planner says
    /// so rather than silently planning one that crosses it. It still returns a
    /// usable plan -- for a freshly chosen target -- but flags that the first
    /// capture will now need a slew.
    /// </summary>
    [Fact]
    public void NearTheMeridian_ThePlanIsNotAnchoredAndSaysWhy()
    {
        TargetPlan plan = PlanFrom(North, rotation: 1.0, declination: 68.0);

        Assert.True(plan.Success, plan.Reason);
        Assert.False(plan.AnchoredAtCurrentPointing);
        Assert.Contains("meridian", plan.Reason!, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Too near the pole the arc stops being distinguishable from a straight
    /// line and the axis becomes unidentifiable (D11). The fit's own curvature
    /// guard would catch it, but catching it here means saying so before the
    /// captures rather than after.
    /// </summary>
    [Theory]
    [InlineData(89.0)]
    [InlineData(80.0)]
    [InlineData(71.0)]
    public void TooNearThePole_ThePlanIsNotAnchoredAndSaysWhy(double declination)
    {
        TargetPlan plan = PlanFrom(North, rotation: 20.0, declination: declination);

        Assert.False(plan.AnchoredAtCurrentPointing);
        Assert.Contains("pole", plan.Reason!, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// The sweep is a request, not a promise. Rather than plan a capture below
    /// the altitude floor, the planner shrinks it -- and reports what it actually
    /// achieved, because uncertainty scales as the inverse square of the sweep
    /// and a shrunk one is a materially worse measurement the user should know
    /// about.
    /// </summary>
    [Fact]
    public void RatherThanPlanSomethingUnusable_TheSweepShrinksAndSaysSo()
    {
        // Far enough west that a full seventy degrees more would take the target
        // well down towards the horizon.
        TargetPlan plan = PlanFrom(North, rotation: 70.0, declination: 50.0, sweep: 70.0);

        Assert.True(plan.Success, plan.Reason);
        Assert.All(plan.Captures, capture =>
            Assert.True(
                capture.PredictedAltitudeDegrees >= TargetSelection.MinimumAltitudeDegrees,
                $"planned a capture at {capture.PredictedAltitudeDegrees:F1}°, below the floor"));

        Assert.InRange(plan.SweepDegrees, TargetSelection.MinimumUsefulSweepDegrees, 70.0);
    }

    /// <summary>
    /// The achieved sweep is the actual angular extent of the plan, not a label.
    /// The UI warns on the strength of this number, so it has to mean what it
    /// says.
    /// </summary>
    [Theory]
    [InlineData(12.0, 68.0, 70.0)]
    [InlineData(20.0, 55.0, 60.0)]
    [InlineData(-15.0, 68.0, 50.0)]
    public void TheReportedSweepMatchesThePlansActualExtent(double rotation, double declination, double requested)
    {
        TargetPlan plan = PlanFrom(North, rotation, declination, sweep: requested);
        Assert.True(plan.Success, plan.Reason);

        double extent = plan.Captures.Max(c => c.MechanicalRotationDegrees)
                        - plan.Captures.Min(c => c.MechanicalRotationDegrees);

        Assert.Equal(plan.SweepDegrees, extent, tolerance: 1e-9);
        Assert.True(plan.SweepDegrees <= requested + 1e-9, "the planner must never exceed the requested sweep");
    }

    /// <summary>
    /// Captures are evenly spread across the sweep. Not for elegance: clustering
    /// them would waste the sweep, which is the parameter uncertainty is most
    /// sensitive to.
    /// </summary>
    [Fact]
    public void CapturesAreEvenlySpacedAcrossTheSweep()
    {
        TargetPlan plan = PlanFrom(North, 12.0, 68.0, captures: 6, sweep: 60.0);
        Assert.True(plan.Success, plan.Reason);

        double[] gaps = plan.Captures
            .Zip(plan.Captures.Skip(1), (a, b) => Math.Abs(b.MechanicalRotationDegrees - a.MechanicalRotationDegrees))
            .ToArray();

        Assert.All(gaps, gap => Assert.Equal(gaps[0], gap, tolerance: 1e-9));
    }

    /// <summary>
    /// Fewer than three captures cannot determine a circle (D7).
    /// </summary>
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public void FewerThanThreeCaptures_IsRefused(int captures)
    {
        TargetPlan plan = PlanFrom(North, 12.0, 68.0, captures: captures);

        Assert.False(plan.Success);
        Assert.Contains("3", plan.Reason!, StringComparison.Ordinal);
    }

    /// <summary>
    /// The southern hemisphere is not an afterthought: the same anchoring has to
    /// work about the south celestial pole.
    ///
    /// Note the sign. Mechanical declination is measured towards the pole the
    /// mount's axis actually points at, so it is *positive* towards the visible
    /// pole in both hemispheres and is not sky declination south of the equator.
    /// </summary>
    [Theory]
    [InlineData(12.0)]
    [InlineData(-25.0)]
    public void TheSouthernHemisphereWorksTheSameWay(double rotation)
    {
        TargetPlan plan = PlanFrom(South, rotation, declination: 68.0);

        Assert.True(plan.Success, plan.Reason);
        Assert.True(plan.AnchoredAtCurrentPointing);
        Assert.Equal(68.0, plan.MechanicalDeclinationDegrees, precision: 9);
        Assert.All(plan.Captures, capture =>
            Assert.True(capture.PredictedAltitudeDegrees >= TargetSelection.MinimumAltitudeDegrees));
    }

    /// <summary>
    /// A *negative* mechanical declination at a southern site is 158° from the
    /// pole the mount turns about -- past its equator, heading for the pole that
    /// never rises there. It must not be anchored, however far from a pole it
    /// technically is.
    /// </summary>
    [Fact]
    public void PointingPastTheMountsEquator_IsNotAnchored()
    {
        TargetPlan plan = PlanFrom(South, rotation: 20.0, declination: -68.0);

        Assert.False(plan.AnchoredAtCurrentPointing);
        Assert.Contains("equator", plan.Reason!, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// A freshly chosen target must land in the pole-distance band the method
    /// actually needs -- roughly 20° to 75° from the axis the mount turns about
    /// (D11) -- in both hemispheres, and must not silently give up sweep to do
    /// it.
    ///
    /// This is a regression test for a real defect. The planner treated
    /// mechanical declination as sky declination, which negated it south of the
    /// equator: at latitude -33.9° it then chose a target 105° from the polar
    /// axis, six degrees above the horizon, and settled for a 30° sweep instead
    /// of the 70° asked for. It reported success throughout, which is precisely
    /// what made it dangerous -- the resulting fit would have been far less
    /// certain than the software implied.
    /// </summary>
    [Theory]
    [InlineData(45.0)]
    [InlineData(-33.9)]
    [InlineData(-52.0)]
    [InlineData(0.5)]
    public void AFreshlyChosenTarget_SitsInTheUsablePoleDistanceBand(double latitude)
    {
        var site = new GeodeticLocation(latitude, 15.0, 200.0);
        TargetPlan plan = TargetSelection.Plan(site, DateTime.UtcNow, 6, 70.0);

        Assert.True(plan.Success, plan.Reason);

        double poleDistance = 90.0 - plan.MechanicalDeclinationDegrees;
        Assert.InRange(poleDistance, TargetSelection.MinimumPoleDistanceDegrees, 75.0);

        Assert.All(plan.Captures, capture =>
            Assert.True(
                capture.PredictedAltitudeDegrees >= TargetSelection.MinimumAltitudeDegrees,
                $"latitude {latitude}: planned a capture at {capture.PredictedAltitudeDegrees:F1}°"));
    }

    /// <summary>
    /// At a useful latitude the full requested sweep is available, and the
    /// planner must not give any of it up.
    ///
    /// Separate from the band test above because near the equator a shrunk sweep
    /// is honest geometry rather than a defect: the pole sits on the horizon, so
    /// a target the right distance from it spends much of its arc too low to
    /// use. Measured, latitude 0.5° yields 50°, and that is the truth about that
    /// site rather than something to be fixed.
    /// </summary>
    [Theory]
    [InlineData(45.0)]
    [InlineData(51.5)]
    [InlineData(-33.9)]
    [InlineData(-52.0)]
    public void AwayFromTheEquator_TheFullSweepIsAvailable(double latitude)
    {
        TargetPlan plan = TargetSelection.Plan(
            new GeodeticLocation(latitude, 15.0, 200.0), DateTime.UtcNow, 6, 70.0);

        Assert.True(plan.Success, plan.Reason);
        Assert.Equal(70.0, plan.SweepDegrees, tolerance: 1e-9);
    }

    /// <summary>
    /// When the direction of the sweep is free, the planner takes the one that
    /// keeps the target higher. Altitude is not the parameter accuracy is most
    /// sensitive to, but at equal sweep it is free to have.
    /// </summary>
    [Fact]
    public void GivenAChoice_TheSweepGoesTheWayThatStaysHigher()
    {
        // Anchored well west, where sweeping further west descends and sweeping
        // back towards the meridian climbs.
        TargetPlan plan = PlanFrom(North, rotation: 55.0, declination: 60.0, sweep: 40.0);
        Assert.True(plan.Success, plan.Reason);

        double furthest = plan.Captures.Max(c => Math.Abs(c.MechanicalRotationDegrees));
        Assert.True(
            furthest <= 55.0 + 1e-9,
            $"the sweep went further west to {furthest:F1}° when turning back towards the meridian was higher");
    }
}
