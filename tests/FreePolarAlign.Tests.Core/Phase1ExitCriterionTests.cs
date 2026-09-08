using FreePolarAlign.Core.Alignment;
using FreePolarAlign.Core.Astrometry;
using Xunit;

namespace FreePolarAlign.Tests.Core;

/// <summary>
/// Phase 1 exit criterion: a parameter sweep over injected misalignments
/// (0.1' to 5 degrees), latitudes including both hemispheres and the equator,
/// and target declinations must recover the injected value to better than 0.1'
/// under realistic solve noise -- and the conditioning must degrade smoothly and
/// predictably as the sweep narrows, with the reported covariance reflecting it.
///
/// The roadmap says that last clause matters more than the accuracy number, so
/// the covariance-calibration tests below are the load-bearing ones: an
/// estimator that is accurate but cannot say how accurate it is would fail this
/// phase, because the software has to know when it does not know.
/// </summary>
public class Phase1ExitCriterionTests
{
    /// <summary>
    /// Per-observation plate-solve accuracy assumed "realistic". A wide-field
    /// guide-scope solve averages over many matched stars, so its field-centre
    /// uncertainty is dominated by WCS and catalogue systematics rather than
    /// centroiding noise; roughly an arcsecond is achievable and two is safe.
    /// Both are exercised here.
    /// </summary>
    private const double GoodSolveNoiseArcseconds = 1.0;
    private const double ConservativeSolveNoiseArcseconds = 2.0;

    /// <summary>
    /// The observing plan the 0.1' criterion actually requires, and it is not a
    /// free choice. Axis uncertainty falls only as the square root of the
    /// capture count but as the *square* of the RA sweep, so sweep width buys
    /// far more than extra captures: measured at 1 arcsecond solve noise, 20
    /// captures over 60 degrees misses the criterion (worst 0.111'), while the
    /// same 20 captures over 70 degrees meets it with margin (worst 0.080').
    /// Thirty captures over 60 degrees also passes, at half again the exposure
    /// time. This plan takes the wider sweep, which still sits inside D8's
    /// 60-75 degree meridian-limited range.
    /// </summary>
    private const double PlanSweepDegrees = 70.0;
    private const int PlanCaptureCount = 20;

    private const double ExitCriterionArcminutes = 0.1;

    private static double[] Sweep(double extentDegrees, int count)
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

    private static double SeparationArcminutes(HorizontalCoordinates a, HorizontalCoordinates b)
    {
        static (double X, double Y, double Z) ToVector(HorizontalCoordinates c)
        {
            double altitude = c.AltitudeDegrees * Math.PI / 180.0;
            double azimuth = c.AzimuthDegrees * Math.PI / 180.0;
            return (Math.Cos(altitude) * Math.Cos(azimuth), Math.Cos(altitude) * Math.Sin(azimuth), Math.Sin(altitude));
        }

        var u = ToVector(a);
        var v = ToVector(b);
        double cx = u.Y * v.Z - u.Z * v.Y;
        double cy = u.Z * v.X - u.X * v.Z;
        double cz = u.X * v.Y - u.Y * v.X;
        double dot = u.X * v.X + u.Y * v.Y + u.Z * v.Z;
        return Math.Atan2(Math.Sqrt(cx * cx + cy * cy + cz * cz), dot) * 180.0 / Math.PI * 60.0;
    }

    /// <summary>
    /// The exit criterion proper, swept over the full stated parameter space:
    /// misalignments from 0.1' to 5 degrees, both hemispheres and the equator,
    /// and target declinations from the equator to near the pole. Measured as
    /// RMS over noise realisations, which is stable, rather than as a single
    /// draw that could pass or fail by luck.
    /// </summary>
    [Theory]
    [InlineData(-70.0)]
    [InlineData(-55.0)]
    [InlineData(-33.9)]
    [InlineData(0.1)]
    [InlineData(20.0)]
    [InlineData(45.0)]
    [InlineData(60.0)]
    [InlineData(70.0)]
    public void ParameterSweep_RecoversInjectedMisalignment_BetterThanTenthArcminute(double latitude)
    {
        const int realizations = 40;
        double worstRms = 0.0;
        string worstCase = "";

        foreach (double magnitudeArcminutes in new[] { 0.1, 1.0, 10.0, 60.0, 300.0 })
        {
            foreach (double declination in new[] { 0.0, 20.0, 45.0, 70.0, 85.0 })
            {
                var injected = new MountMisalignment(
                    magnitudeArcminutes * 0.6,
                    magnitudeArcminutes * 0.8);
                HorizontalCoordinates truth = MountForwardModel.MountAxis(latitude, injected);

                var random = new Random(HashCode.Combine(latitude, magnitudeArcminutes, declination));
                double sumSquares = 0.0;

                for (int realization = 0; realization < realizations; realization++)
                {
                    var clean = MountForwardModel.PointingSequence(
                        latitude, injected, declination, coneErrorArcminutes: 0.0, conePhaseDegrees: 0.0,
                        Sweep(PlanSweepDegrees, PlanCaptureCount));
                    var noisy = clean
                        .Select(c => MountForwardModel.AddNoise(c, GoodSolveNoiseArcseconds, random))
                        .ToArray();

                    PolarAlignmentSolution solution = PolarAlignmentSolver.Solve(noisy, latitude, GoodSolveNoiseArcseconds);
                    double error = SeparationArcminutes(solution.MountAxis, truth);
                    sumSquares += error * error;
                }

                double rms = Math.Sqrt(sumSquares / realizations);
                if (rms > worstRms)
                {
                    worstRms = rms;
                    worstCase = $"misalignment {magnitudeArcminutes}', declination {declination} deg";
                }
            }
        }

        Assert.True(worstRms < ExitCriterionArcminutes,
            $"latitude {latitude}: worst RMS axis error {worstRms:F4}' exceeded {ExitCriterionArcminutes}' at {worstCase}");
    }

    /// <summary>
    /// Cone error must not degrade recovery under noise either -- not just in
    /// the noiseless case. A guide scope squared up by hand can easily be a
    /// degree off, and the method's whole appeal is that this does not matter.
    /// </summary>
    [Theory]
    [InlineData(0.0)]
    [InlineData(30.0)]
    [InlineData(120.0)]
    public void ExitCriterion_HoldsWithLargeConeError(double coneErrorArcminutes)
    {
        const double latitude = 45.0;
        const int realizations = 60;
        var injected = new MountMisalignment(6.0, 8.0);
        HorizontalCoordinates truth = MountForwardModel.MountAxis(latitude, injected);

        var random = new Random(20260908);
        double sumSquares = 0.0;

        for (int realization = 0; realization < realizations; realization++)
        {
            var clean = MountForwardModel.PointingSequence(
                latitude, injected, 20.0, coneErrorArcminutes, conePhaseDegrees: 53.0,
                Sweep(PlanSweepDegrees, PlanCaptureCount));
            var noisy = clean.Select(c => MountForwardModel.AddNoise(c, GoodSolveNoiseArcseconds, random)).ToArray();

            PolarAlignmentSolution solution = PolarAlignmentSolver.Solve(noisy, latitude, GoodSolveNoiseArcseconds);
            double error = SeparationArcminutes(solution.MountAxis, truth);
            sumSquares += error * error;
        }

        double rms = Math.Sqrt(sumSquares / realizations);
        Assert.True(rms < ExitCriterionArcminutes,
            $"cone error {coneErrorArcminutes}': RMS axis error {rms:F4}' exceeded {ExitCriterionArcminutes}'");
    }

    /// <summary>
    /// The clause that matters most: the reported uncertainty must match the
    /// error the estimator actually makes. Checked component by component --
    /// altitude and azimuth separately, since they have genuinely different
    /// uncertainties -- across latitudes, geometries and noise levels.
    /// </summary>
    [Theory]
    [InlineData(45.0, 60.0, 8, 2.0)]
    [InlineData(60.0, 45.0, 6, 2.0)]
    [InlineData(-33.9, 60.0, 10, 1.0)]
    [InlineData(0.1, 75.0, 5, 2.0)]
    [InlineData(70.0, 30.0, 12, 1.0)]
    [InlineData(-70.0, 60.0, 20, 0.5)]
    public void ReportedUncertainty_MatchesObservedScatter(double latitude, double sweepDegrees, int captures, double noiseArcseconds)
    {
        const int realizations = 1500;
        var injected = new MountMisalignment(7.0, -9.0);
        var random = new Random(HashCode.Combine(latitude, sweepDegrees, captures));

        double sumSquaredAltitude = 0.0, sumSquaredAzimuth = 0.0;
        double sumReportedAltitude = 0.0, sumReportedAzimuth = 0.0;

        for (int realization = 0; realization < realizations; realization++)
        {
            var clean = MountForwardModel.PointingSequence(
                latitude, injected, 20.0, 0.0, 0.0, Sweep(sweepDegrees, captures));
            var noisy = clean.Select(c => MountForwardModel.AddNoise(c, noiseArcseconds, random)).ToArray();

            PolarAlignmentSolution solution = PolarAlignmentSolver.Solve(noisy, latitude, noiseArcseconds);

            double altitudeDeviation = solution.AltitudeErrorArcminutes - injected.AltitudeErrorArcminutes;
            double azimuthDeviation = solution.AzimuthErrorArcminutes - injected.AzimuthErrorArcminutes;
            sumSquaredAltitude += altitudeDeviation * altitudeDeviation;
            sumSquaredAzimuth += azimuthDeviation * azimuthDeviation;
            sumReportedAltitude += solution.AltitudeSigmaArcminutes;
            sumReportedAzimuth += solution.AzimuthSigmaArcminutes;
        }

        double observedAltitude = Math.Sqrt(sumSquaredAltitude / realizations);
        double observedAzimuth = Math.Sqrt(sumSquaredAzimuth / realizations);
        double reportedAltitude = sumReportedAltitude / realizations;
        double reportedAzimuth = sumReportedAzimuth / realizations;

        // A covariance that is merely the right order of magnitude would not be
        // good enough to withhold results on, so this is tight: 15% either way,
        // which also catches an over-confident estimator, not just a vague one.
        Assert.InRange(observedAltitude / reportedAltitude, 0.85, 1.15);
        Assert.InRange(observedAzimuth / reportedAzimuth, 0.85, 1.15);
    }

    /// <summary>
    /// The same calibration check for the total error, which is the figure the
    /// 10 arcminute success indication is judged on and therefore the one whose
    /// uncertainty a user implicitly relies on.
    /// </summary>
    [Theory]
    [InlineData(45.0, 20.0)]
    [InlineData(60.0, 20.0)]
    [InlineData(-33.9, 20.0)]
    [InlineData(45.0, 120.0)]
    [InlineData(0.1, 20.0)]
    public void ReportedTotalUncertainty_MatchesObservedScatter(double latitude, double magnitudeArcminutes)
    {
        const int realizations = 2000;
        var injected = new MountMisalignment(magnitudeArcminutes * 0.6, magnitudeArcminutes * 0.8);
        var random = new Random(HashCode.Combine(latitude, magnitudeArcminutes, 991));

        var totals = new List<double>(realizations);
        double sumReported = 0.0;

        for (int realization = 0; realization < realizations; realization++)
        {
            var clean = MountForwardModel.PointingSequence(latitude, injected, 20.0, 0.0, 0.0, Sweep(60.0, 8));
            var noisy = clean.Select(c => MountForwardModel.AddNoise(c, ConservativeSolveNoiseArcseconds, random)).ToArray();

            PolarAlignmentSolution solution = PolarAlignmentSolver.Solve(noisy, latitude, ConservativeSolveNoiseArcseconds);
            totals.Add(solution.TotalErrorArcminutes);
            sumReported += solution.TotalSigmaArcminutes;
        }

        double mean = totals.Average();
        double observed = Math.Sqrt(totals.Sum(t => (t - mean) * (t - mean)) / totals.Count);
        double reported = sumReported / realizations;

        Assert.InRange(observed / reported, 0.85, 1.15);
    }

    /// <summary>
    /// Conditioning must degrade *predictably*, not merely degrade. The
    /// near-degenerate axis-along-arc versus radius combination is broken only
    /// at second order in the sweep, so its eigenvalue falls as the fourth
    /// power and the condition number rises as the fourth power. Asserting the
    /// exponent rather than a threshold is what makes this a statement about
    /// behaviour instead of a snapshot.
    /// </summary>
    [Fact]
    public void ConditionNumber_RisesAsFourthPowerOfNarrowingSweep()
    {
        const double latitude = 45.0;
        var injected = new MountMisalignment(6.0, 8.0);

        double? previousCondition = null;
        double previousSweep = 0.0;

        foreach (double sweep in new[] { 120.0, 60.0, 30.0, 15.0, 7.5 })
        {
            var observations = MountForwardModel.PointingSequence(
                latitude, injected, 20.0, 0.0, 0.0, Sweep(sweep, 8));
            SmallCircleFit fit = SmallCircleFitter.Fit(observations, MountForwardModel.NominalPole(latitude), 2.0);

            if (previousCondition.HasValue)
            {
                double exponent = Math.Log(fit.Uncertainty.ConditionNumber / previousCondition.Value)
                                / Math.Log(previousSweep / sweep);
                Assert.InRange(exponent, 3.8, 4.4);
            }

            previousCondition = fit.Uncertainty.ConditionNumber;
            previousSweep = sweep;
        }
    }

    [Fact]
    public void AxisUncertainty_RisesAsSquareOfNarrowingSweep()
    {
        const double latitude = 45.0;
        var injected = new MountMisalignment(6.0, 8.0);

        double? previousSigma = null;
        double previousSweep = 0.0;

        foreach (double sweep in new[] { 120.0, 60.0, 30.0, 15.0, 7.5 })
        {
            var observations = MountForwardModel.PointingSequence(
                latitude, injected, 20.0, 0.0, 0.0, Sweep(sweep, 8));
            SmallCircleFit fit = SmallCircleFitter.Fit(observations, MountForwardModel.NominalPole(latitude), 2.0);

            if (previousSigma.HasValue)
            {
                double exponent = Math.Log(fit.Uncertainty.AltitudeSigmaArcseconds / previousSigma.Value)
                                / Math.Log(previousSweep / sweep);
                Assert.InRange(exponent, 1.85, 2.15);
            }

            previousSigma = fit.Uncertainty.AltitudeSigmaArcseconds;
            previousSweep = sweep;
        }
    }

    /// <summary>
    /// Monotonic, not merely asymptotically correct: a user narrowing their
    /// sweep must never see the reported confidence improve.
    /// </summary>
    [Fact]
    public void AxisUncertainty_IncreasesMonotonicallyAsSweepNarrows()
    {
        double previous = 0.0;
        foreach (double sweep in new[] { 150.0, 120.0, 90.0, 75.0, 60.0, 45.0, 30.0, 20.0, 15.0, 10.0, 5.0 })
        {
            var observations = MountForwardModel.PointingSequence(
                45.0, new MountMisalignment(6.0, 8.0), 20.0, 0.0, 0.0, Sweep(sweep, 8));
            SmallCircleFit fit = SmallCircleFitter.Fit(observations, MountForwardModel.NominalPole(45.0), 2.0);

            Assert.True(fit.Uncertainty.AltitudeSigmaArcseconds > previous,
                $"sweep {sweep} reported sigma {fit.Uncertainty.AltitudeSigmaArcseconds:F3}\" " +
                $"which is not worse than the wider sweep's {previous:F3}\"");
            previous = fit.Uncertainty.AltitudeSigmaArcseconds;
        }
    }

    /// <summary>
    /// A sweep too narrow to support an answer must be reported as such and
    /// withheld, rather than returning a confident number (D11).
    /// </summary>
    [Theory]
    [InlineData(5.0)]
    [InlineData(2.0)]
    [InlineData(1.0)]
    public void SweepTooNarrowToSolve_IsWithheld(double sweepDegrees)
    {
        var observations = MountForwardModel.PointingSequence(
            45.0, new MountMisalignment(6.0, 8.0), 20.0, 0.0, 0.0, Sweep(sweepDegrees, 8));

        PolarAlignmentSolution solution = PolarAlignmentSolver.Solve(observations, 45.0, 2.0);

        Assert.False(solution.IsTrustworthy, $"a {sweepDegrees} degree sweep should not be trusted");
        Assert.Contains("conditioned", solution.UntrustworthyReason!, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// The roadmap expects conditioning to degrade "as the target approaches the
    /// pole". Measured, that is not what happens, and the difference matters.
    ///
    /// The circle's radius does not enter the fit's conditioning at all -- only
    /// the spread of rotation angles does -- so recovery is flat from the
    /// celestial equator to within 6 arcminutes of the pole, across four orders
    /// of magnitude of radius. What does eventually happen is not graceful
    /// degradation but a loss of identifiability: once the arc cannot be told
    /// from a straight line, a huge circle centred far away fits better than the
    /// true one, and the fit's median stays fine while its tail reaches degrees.
    /// So the pole is dangerous in a different way than the roadmap implies, and
    /// the answer is to refuse rather than to widen an error bar.
    /// </summary>
    [Fact]
    public void TargetDeclination_DoesNotAffectRecovery_AcrossThePracticalRange()
    {
        const double latitude = 45.0;
        const double noise = 2.0;
        const int realizations = 200;
        var injected = new MountMisalignment(6.0, 8.0);
        HorizontalCoordinates truth = MountForwardModel.MountAxis(latitude, injected);

        double RmsFor(double declination)
        {
            var random = new Random(31337);
            double sumSquares = 0.0;
            for (int realization = 0; realization < realizations; realization++)
            {
                var clean = MountForwardModel.PointingSequence(
                    latitude, injected, declination, 0.0, 0.0, Sweep(60.0, 8));
                var noisy = clean.Select(c => MountForwardModel.AddNoise(c, noise, random)).ToArray();
                PolarAlignmentSolution solution = PolarAlignmentSolver.Solve(noisy, latitude, noise);
                Assert.True(solution.IsTrustworthy, $"declination {declination} should be solvable: {solution.UntrustworthyReason}");
                sumSquares += Math.Pow(SeparationArcminutes(solution.MountAxis, truth), 2);
            }

            return Math.Sqrt(sumSquares / realizations);
        }

        double atEquator = RmsFor(0.0);
        foreach (double declination in new[] { 20.0, 45.0, 80.0, 89.0, 89.9 })
        {
            Assert.InRange(RmsFor(declination) / atEquator, 0.8, 1.25);
        }
    }

    /// <summary>
    /// The unidentifiable regime must be refused, not reported. Pointing within
    /// an arcminute or two of the mount's own axis leaves an arc that cannot be
    /// distinguished from a straight line, and an unguarded fit there returns
    /// answers wrong by up to two degrees while leaving small residuals and a
    /// healthy condition number -- the exact shape of failure D11 exists to stop.
    ///
    /// Two things are asserted: that such geometry is overwhelmingly withheld,
    /// and -- more importantly -- that whatever does slip through is still
    /// accurate. A guard that merely withheld *often* would not be enough.
    /// </summary>
    [Theory]
    [InlineData(2.0)]
    [InlineData(10.0)]
    [InlineData(20.0)]
    [InlineData(40.0)]
    public void UnresolvableCircle_IsWithheldRatherThanReportedWrongly(double radiusArcseconds)
    {
        const double latitude = 45.0;
        const double noise = 2.0;
        const int realizations = 500;
        var injected = new MountMisalignment(6.0, 8.0);
        HorizontalCoordinates truth = MountForwardModel.MountAxis(latitude, injected);
        double declination = 90.0 - radiusArcseconds / 3600.0;

        var random = new Random(2718);
        int withheld = 0;
        double worstTrustedError = 0.0;

        for (int realization = 0; realization < realizations; realization++)
        {
            var clean = MountForwardModel.PointingSequence(
                latitude, injected, declination, 0.0, 0.0, Sweep(60.0, 8));
            var noisy = clean.Select(c => MountForwardModel.AddNoise(c, noise, random)).ToArray();

            PolarAlignmentSolution solution = PolarAlignmentSolver.Solve(noisy, latitude, noise);
            if (!solution.IsTrustworthy)
            {
                withheld++;
                continue;
            }

            worstTrustedError = Math.Max(worstTrustedError, SeparationArcminutes(solution.MountAxis, truth));
        }

        Assert.True(withheld >= (int)(0.95 * realizations),
            $"radius {radiusArcseconds}\": only {withheld}/{realizations} were withheld");

        // Nothing that survives the guard may be catastrophically wrong.
        Assert.True(worstTrustedError < 1.0,
            $"radius {radiusArcseconds}\": a trusted result was wrong by {worstTrustedError:F3}'");
    }

    /// <summary>
    /// The guard must not fire on real observing geometry, where the circle
    /// radius exceeds the solve noise by three or four orders of magnitude.
    /// </summary>
    [Theory]
    [InlineData(0.0)]
    [InlineData(20.0)]
    [InlineData(45.0)]
    [InlineData(70.0)]
    [InlineData(85.0)]
    public void RealisticGeometry_IsNeverWithheld(double declination)
    {
        var random = new Random(4711);
        for (int realization = 0; realization < 200; realization++)
        {
            var clean = MountForwardModel.PointingSequence(
                45.0, new MountMisalignment(6.0, 8.0), declination, 0.0, 0.0, Sweep(60.0, 8));
            var noisy = clean.Select(c => MountForwardModel.AddNoise(c, 2.0, random)).ToArray();

            PolarAlignmentSolution solution = PolarAlignmentSolver.Solve(noisy, 45.0, 2.0);
            Assert.True(solution.IsTrustworthy, $"declination {declination}: {solution.UntrustworthyReason}");
        }
    }
}
