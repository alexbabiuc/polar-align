using FreePolarAlign.Core.Alignment;
using FreePolarAlign.Core.Astrometry;
using Xunit;

namespace FreePolarAlign.Tests.Core;

/// <summary>
/// Geometric properties of the fit that hold exactly, independent of noise.
/// These are the claims the README makes about the method, tested as claims
/// rather than assumed.
/// </summary>
public class AlignmentGeometryTests
{
    private static IReadOnlyList<double> Sweep(double extentDegrees, int count)
    {
        if (count == 1)
        {
            return new[] { 0.0 };
        }

        var angles = new double[count];
        double step = extentDegrees / (count - 1);
        for (int i = 0; i < count; i++)
        {
            angles[i] = -extentDegrees / 2.0 + i * step;
        }

        return angles;
    }

    [Theory]
    [InlineData(45.0, 12.0, -7.0)]
    [InlineData(60.0, -30.0, 45.0)]
    [InlineData(0.1, 3.0, 3.0)]
    [InlineData(-33.9, 20.0, -15.0)]
    [InlineData(-55.0, -120.0, 90.0)]
    public void NoiselessFit_RecoversInjectedMisalignmentExactly(double latitude, double altitudeError, double azimuthError)
    {
        var injected = new MountMisalignment(altitudeError, azimuthError);
        var observations = MountForwardModel.PointingSequence(
            latitude, injected, declinationDegrees: 20.0,
            coneErrorArcminutes: 0.0, conePhaseDegrees: 0.0,
            Sweep(60.0, 5));

        PolarAlignmentSolution solution = PolarAlignmentSolver.Solve(observations, latitude, expectedNoiseArcseconds: 2.0);

        Assert.Equal(altitudeError, solution.AltitudeErrorArcminutes, precision: 6);
        Assert.Equal(azimuthError, solution.AzimuthErrorArcminutes, precision: 6);
        Assert.True(solution.IsTrustworthy, solution.UntrustworthyReason);
    }

    /// <summary>
    /// The README's central claim: cone error cannot affect the recovered axis,
    /// because the optical axis is simply fixed in the rotating mount frame.
    /// Swept over cone magnitudes far larger than any real instrument and over
    /// every offset direction, the axis must come back identical.
    /// </summary>
    [Theory]
    [InlineData(0.0)]
    [InlineData(5.0)]
    [InlineData(60.0)]
    [InlineData(300.0)]
    public void ConeError_DoesNotAffectRecoveredAxis(double coneErrorArcminutes)
    {
        const double latitude = 45.0;
        var injected = new MountMisalignment(15.0, -25.0);

        foreach (double phase in new[] { 0.0, 37.0, 90.0, 180.0, 271.0 })
        {
            var observations = MountForwardModel.PointingSequence(
                latitude, injected, declinationDegrees: 30.0,
                coneErrorArcminutes, phase,
                Sweep(60.0, 5));

            PolarAlignmentSolution solution = PolarAlignmentSolver.Solve(observations, latitude, expectedNoiseArcseconds: 2.0);

            Assert.Equal(15.0, solution.AltitudeErrorArcminutes, precision: 6);
            Assert.Equal(-25.0, solution.AzimuthErrorArcminutes, precision: 6);
        }
    }

    /// <summary>
    /// Cone error must still change the circle's radius -- otherwise the test
    /// above would be passing because the cone error is being silently ignored
    /// rather than genuinely absorbed.
    /// </summary>
    [Fact]
    public void ConeError_DoesChangeFittedRadius()
    {
        const double latitude = 45.0;
        var injected = new MountMisalignment(15.0, -25.0);

        SmallCircleFit WithCone(double coneArcminutes) => SmallCircleFitter.Fit(
            MountForwardModel.PointingSequence(latitude, injected, 30.0, coneArcminutes, 0.0, Sweep(60.0, 5)),
            MountForwardModel.NominalPole(latitude),
            2.0);

        double radiusWithout = WithCone(0.0).RadiusDegrees;
        double radiusWith = WithCone(60.0).RadiusDegrees;

        Assert.Equal(60.0, radiusWithout, precision: 6);
        Assert.True(Math.Abs(radiusWith - radiusWithout) > 0.9,
            $"cone error of 60' should move the radius by about a degree, moved {Math.Abs(radiusWith - radiusWithout):F4} deg");
    }

    /// <summary>
    /// Three points determine the circle exactly, so the fit has no degrees of
    /// freedom and its residuals carry no information. A fourth point is what
    /// buys the residual check (D7).
    /// </summary>
    [Fact]
    public void ThreePoints_FitExactlyButLeaveNoResidualCheck()
    {
        const double latitude = 45.0;
        var injected = new MountMisalignment(8.0, 6.0);

        var three = MountForwardModel.PointingSequence(latitude, injected, 20.0, 0.0, 0.0, Sweep(60.0, 3));
        SmallCircleFit fit = SmallCircleFitter.Fit(three, MountForwardModel.NominalPole(latitude), 2.0);

        Assert.Equal(0, fit.DegreesOfFreedom);
        Assert.True(fit.ResidualRmsArcseconds < 1e-6, $"exact fit should have no residuals, got {fit.ResidualRmsArcseconds}\"");

        var four = MountForwardModel.PointingSequence(latitude, injected, 20.0, 0.0, 0.0, Sweep(60.0, 4));
        Assert.Equal(1, SmallCircleFitter.Fit(four, MountForwardModel.NominalPole(latitude), 2.0).DegreesOfFreedom);
    }

    [Fact]
    public void FewerThanThreePoints_IsRejected()
    {
        var two = new[] { new HorizontalCoordinates(10, 45), new HorizontalCoordinates(20, 46) };
        Assert.Throws<ArgumentException>(() => SmallCircleFitter.Fit(two, new HorizontalCoordinates(0, 45), 2.0));
    }

    /// <summary>
    /// The azimuth bolt figure and the on-sky angle differ by 1/cos(altitude)
    /// (D12). Confusing them would misreport how far the user has to turn the
    /// bolt, badly so at high latitude.
    /// </summary>
    [Fact]
    public void AzimuthUncertainty_ScalesWithInverseCosineOfAltitude()
    {
        foreach (double latitude in new[] { 20.0, 45.0, 65.0 })
        {
            var observations = MountForwardModel.PointingSequence(
                latitude, new MountMisalignment(5.0, 5.0), 20.0, 0.0, 0.0, Sweep(60.0, 6));
            SmallCircleFit fit = SmallCircleFitter.Fit(observations, MountForwardModel.NominalPole(latitude), 2.0);

            double expectedRatio = 1.0 / Math.Cos(fit.Axis.AltitudeDegrees * Math.PI / 180.0);
            double actualRatio = fit.Uncertainty.AzimuthAngleSigmaArcseconds / fit.Uncertainty.AzimuthOnSkySigmaArcseconds;

            Assert.Equal(expectedRatio, actualRatio, precision: 6);
        }
    }

    /// <summary>
    /// A declination nudge between captures breaks the rigid-body assumption,
    /// so the points no longer lie on one circle. This is the failure mode that
    /// produces a confident wrong answer with nothing on screen to warn the
    /// user, so it must be caught from the residuals alone (D11).
    /// </summary>
    [Theory]
    [InlineData(30.0)]
    [InlineData(60.0)]
    [InlineData(300.0)]
    public void DeclinationNudgeMidSequence_IsDetectedAndWithheld(double nudgeArcseconds)
    {
        const double latitude = 45.0;
        const double noise = 2.0;
        var injected = new MountMisalignment(10.0, -8.0);
        var angles = Sweep(60.0, 6);

        // First half at one declination, second half nudged: exactly what
        // happens if the declination clutch slips or is bumped.
        var first = MountForwardModel.PointingSequence(
            latitude, injected, 20.0, 0.0, 0.0, angles.Take(3).ToArray());
        var second = MountForwardModel.PointingSequence(
            latitude, injected, 20.0 + nudgeArcseconds / 3600.0, 0.0, 0.0, angles.Skip(3).ToArray());

        var observations = first.Concat(second).ToArray();
        PolarAlignmentSolution solution = PolarAlignmentSolver.Solve(observations, latitude, noise);

        Assert.False(solution.IsTrustworthy,
            $"a {nudgeArcseconds}\" declination nudge against {noise}\" solve noise should be caught; " +
            $"residual RMS was {solution.Fit.ResidualRmsArcseconds:F2}\"");
        Assert.Contains("declination", solution.UntrustworthyReason!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void CleanSequenceWithNoise_IsNotFalselyWithheld()
    {
        const double latitude = 45.0;
        const double noise = 2.0;
        var injected = new MountMisalignment(10.0, -8.0);
        var random = new Random(12345);

        int falsePositives = 0;
        for (int trial = 0; trial < 200; trial++)
        {
            var clean = MountForwardModel.PointingSequence(latitude, injected, 20.0, 0.0, 0.0, Sweep(60.0, 6));
            var noisy = clean.Select(c => MountForwardModel.AddNoise(c, noise, random)).ToArray();

            if (!PolarAlignmentSolver.Solve(noisy, latitude, noise).IsTrustworthy)
            {
                falsePositives++;
            }
        }

        // The chi-square limit is deliberately loose; withholding a good result
        // trains users to ignore the warning, which costs more than it saves.
        Assert.True(falsePositives <= 4, $"{falsePositives}/200 clean sequences were withheld");
    }
}
