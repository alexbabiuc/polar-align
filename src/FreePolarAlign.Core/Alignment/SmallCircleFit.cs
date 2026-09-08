using FreePolarAlign.Core.Astrometry;

namespace FreePolarAlign.Core.Alignment;

/// <summary>
/// How well the fitted axis is pinned down, and in which direction it is not.
///
/// The two sigmas are not interchangeable. <see cref="AltitudeSigmaArcseconds"/>
/// and <see cref="AzimuthOnSkySigmaArcseconds"/> are angular distances on the
/// sky, so they are what the total misalignment is built from.
/// <see cref="AzimuthAngleSigmaArcseconds"/> is the uncertainty in the azimuth
/// *angle* -- the thing an azimuth bolt changes -- and is larger than the
/// on-sky figure by 1/cos(axis altitude) (D12: "apparent field motion scales
/// with cos(altitude)"). At high latitudes the difference is substantial, and
/// reporting the wrong one to the user would overstate how much bolt travel is
/// needed.
/// </summary>
/// <param name="ConditionNumber">
/// Ratio of largest to smallest eigenvalue of the fit's normal matrix. Grows as
/// the **fourth** power of the inverse RA sweep, with the axis uncertainty
/// growing as the square -- both measured, and asymptotically exact. A short arc
/// cannot separate "the axis moved along the arc" from "the circle's radius
/// changed": that near-degenerate combination is broken only at second order in
/// the sweep, so its eigenvalue falls as sweep^4. It is also the geometric
/// reason D7 requires three points rather than two, since with two the
/// combination is not merely ill-conditioned but exactly singular.
/// </param>
public sealed record AxisUncertainty(
    double AltitudeSigmaArcseconds,
    double AzimuthOnSkySigmaArcseconds,
    double AzimuthAngleSigmaArcseconds,
    double Correlation,
    double ConditionNumber);

/// <summary>
/// A small circle fitted to observed pointing directions: its axis is the
/// mount's mechanical polar axis.
/// </summary>
/// <param name="Axis">The fitted rotation axis, in the local horizon frame.</param>
/// <param name="RadiusDegrees">
/// Angular radius of the circle, i.e. the angle between the optical axis and
/// the mount's polar axis. Absorbs cone error entirely, which is why cone error
/// needs no modelling or calibration anywhere in this project.
/// </param>
/// <param name="DegreesOfFreedom">
/// Observations minus the three fitted parameters. Zero at exactly three
/// observations -- the fit is then exact and its residuals carry no information,
/// so a fourth observation is what buys the residual check (D7, D11).
/// </param>
/// <param name="ChiSquare">
/// Sum of squared residuals in units of the expected solve noise. Compared
/// against <see cref="DegreesOfFreedom"/> it is the statistic that detects a
/// declination nudge or meridian flip mid-sequence (D11): those break the
/// rigid-body assumption, so the points no longer lie on one circle and the
/// residuals inflate far beyond solve noise.
/// </param>
/// <param name="CurvatureSignalToNoise">
/// How clearly the captures curve at all: the arc's sagitta,
/// radius * (1 - cos(sweep / 2)), in units of the solve noise it has to be seen
/// against, improved by the square root of the capture count.
///
/// This is a second, independent way for the geometry to fail, and it is not
/// visible in <see cref="AxisUncertainty.ConditionNumber"/>. When an arc cannot
/// be told from a straight line, a very large circle centred far away fits a
/// tight cluster of points with *lower* residuals than the true small circle
/// does -- so the likelihood becomes multimodal, the axis is not identifiable,
/// and the fit can return an answer wrong by degrees while leaving small
/// residuals and a healthy condition number. No amount of care in the optimiser
/// removes that; it has to be detected and refused (D11).
/// </param>
public sealed record SmallCircleFit(
    HorizontalCoordinates Axis,
    double RadiusDegrees,
    IReadOnlyList<double> ResidualsArcseconds,
    double ResidualRmsArcseconds,
    int DegreesOfFreedom,
    double ChiSquare,
    AxisUncertainty Uncertainty,
    double CurvatureSignalToNoise,
    bool IsWellConditioned,
    bool IsCircleResolved);

/// <summary>
/// Fits a small circle on the sphere to a set of observed pointing directions.
///
/// The mount is a rigid body in the ground frame, so as it rotates in RA the
/// optical axis traces an exact circle whose axis is the mount's polar axis.
/// Note that this holds for the *physical pointing directions* -- refraction
/// changes which star appears at the field centre, not where the tube points,
/// so it does not distort the circle. Callers should therefore convert each
/// plate solve to a horizon-frame direction through the full apparent-place
/// transform (<see cref="TopocentricConverter"/>, refraction included) and fit
/// those, rather than fitting catalogue coordinates and comparing against a
/// notional "refracted pole".
///
/// A circle on a sphere is the sphere's intersection with a plane, so an
/// initial axis comes from a total-least-squares plane fit (the smallest
/// eigenvector of the observations' scatter matrix). That estimate minimises
/// an algebraic residual rather than an angular one, so it is refined by
/// Gauss-Newton on true angular residuals, which is also what makes the
/// reported covariance meaningful.
/// </summary>
public static class SmallCircleFitter
{
    private const double DegreesToRadians = Math.PI / 180.0;
    private const double RadiansToArcseconds = 180.0 * 3600.0 / Math.PI;

    /// <summary>
    /// Minimum observations. Three positions determine a circle exactly; two
    /// determine an arc but not where its centre lies along the perpendicular
    /// bisector, and closing that gap would mean trusting the mount's reported
    /// slew angle and importing its gearing error (D7).
    /// </summary>
    public const int MinimumObservations = 3;

    /// <summary>
    /// Above this condition number the fit is reported as ill-conditioned. The
    /// estimate is still returned -- withholding is the caller's decision, and
    /// it needs the numbers to explain itself to the user -- but
    /// <see cref="SmallCircleFit.IsWellConditioned"/> is false.
    /// </summary>
    public const double ConditionNumberLimit = 1.0e6;

    /// <summary>
    /// Minimum <see cref="SmallCircleFit.CurvatureSignalToNoise"/> for the
    /// circle to be considered identifiable.
    ///
    /// Calibrated by measurement rather than derived: sweeping the circle radius
    /// against solve noise, recovery is clean above about 15 (worst error over
    /// 2000 trials stayed near an arcminute) and breaks down below about 8
    /// (worst error reached two degrees, with the fitted radius running away by
    /// orders of magnitude). The limit sits at the clean end of that transition.
    /// Real observing geometry clears it by three or four orders of magnitude --
    /// reaching this regime means pointing within an arcminute or two of the
    /// mount's own axis -- so it costs nothing in practice and converts an
    /// otherwise silent catastrophic failure into a refusal.
    /// </summary>
    public const double CurvatureSignalToNoiseLimit = 15.0;

    /// <param name="observations">Physical pointing directions, horizon frame.</param>
    /// <param name="nominalAxis">
    /// Roughly where the axis is expected (the visible celestial pole). Required
    /// rather than inferred: a fitted plane has two normals, and when the circle
    /// approaches a great circle -- the optical axis 90 degrees from the mount
    /// axis -- the data alone cannot choose between them.
    /// </param>
    /// <param name="expectedNoiseArcseconds">
    /// Per-observation plate-solve accuracy. Sets the scale of the reported
    /// covariance, and the yardstick the chi-square is measured against.
    /// </param>
    public static SmallCircleFit Fit(
        IReadOnlyList<HorizontalCoordinates> observations,
        HorizontalCoordinates nominalAxis,
        double expectedNoiseArcseconds)
    {
        ArgumentNullException.ThrowIfNull(observations);
        if (observations.Count < MinimumObservations)
        {
            throw new ArgumentException(
                $"A small-circle fit needs at least {MinimumObservations} observations (D7); got {observations.Count}.",
                nameof(observations));
        }

        if (expectedNoiseArcseconds <= 0.0 || !double.IsFinite(expectedNoiseArcseconds))
        {
            throw new ArgumentOutOfRangeException(nameof(expectedNoiseArcseconds), expectedNoiseArcseconds,
                "Expected solve noise must be a positive, finite number of arcseconds.");
        }

        int n = observations.Count;
        var directions = new Vector3[n];
        for (int i = 0; i < n; i++)
        {
            directions[i] = ToHorizonVector(observations[i]);
        }

        Vector3 nominal = ToHorizonVector(nominalAxis);
        Vector3 axis = InitialAxis(directions, nominal);
        double radius = InitialRadius(directions, axis);

        (axis, radius) = Refine(directions, axis, radius);

        double noiseRadians = expectedNoiseArcseconds / RadiansToArcseconds;
        var residuals = new double[n];
        double sumSquares = 0.0;
        for (int i = 0; i < n; i++)
        {
            double residual = AngleBetween(directions[i], axis) - radius;
            residuals[i] = residual * RadiansToArcseconds;
            sumSquares += residual * residual;
        }

        int degreesOfFreedom = n - 3;
        double chiSquare = sumSquares / (noiseRadians * noiseRadians);
        double rms = Math.Sqrt(sumSquares / n) * RadiansToArcseconds;

        AxisUncertainty uncertainty = ComputeUncertainty(directions, axis, noiseRadians);
        double curvatureSignalToNoise = CurvatureSignal(directions, axis, radius, noiseRadians);

        return new SmallCircleFit(
            ToHorizontalCoordinates(axis),
            radius / DegreesToRadians,
            residuals,
            rms,
            degreesOfFreedom,
            chiSquare,
            uncertainty,
            curvatureSignalToNoise,
            uncertainty.ConditionNumber <= ConditionNumberLimit,
            curvatureSignalToNoise >= CurvatureSignalToNoiseLimit);
    }

    /// <summary>
    /// Sagitta of the observed arc measured in solve-noise units: how strongly
    /// the captures declare themselves to be curved rather than straight.
    /// </summary>
    private static double CurvatureSignal(Vector3[] directions, Vector3 axis, double radius, double noiseRadians)
    {
        double extent = AzimuthalExtent(directions, axis);
        double sagitta = radius * (1.0 - Math.Cos(extent / 2.0));
        return sagitta * Math.Sqrt(directions.Length) / noiseRadians;
    }

    /// <summary>
    /// Angular extent the captures span around the axis, found as the complement
    /// of their largest angular gap so that a sequence straddling the wrap point
    /// is measured correctly.
    /// </summary>
    private static double AzimuthalExtent(Vector3[] directions, Vector3 axis)
    {
        var (eAlt, eAz) = TangentBasis(axis);
        var angles = new List<double>(directions.Length);

        foreach (Vector3 d in directions)
        {
            Vector3 tangent = d - d.Dot(axis) * axis;
            if (tangent.Length < 1e-15)
            {
                continue;
            }

            double angle = Math.Atan2(tangent.Dot(eAz), tangent.Dot(eAlt));
            angles.Add(angle < 0 ? angle + 2.0 * Math.PI : angle);
        }

        if (angles.Count < 2)
        {
            return 0.0;
        }

        angles.Sort();
        double largestGap = 2.0 * Math.PI - (angles[^1] - angles[0]);
        for (int i = 1; i < angles.Count; i++)
        {
            largestGap = Math.Max(largestGap, angles[i] - angles[i - 1]);
        }

        return 2.0 * Math.PI - largestGap;
    }

    private static Vector3 InitialAxis(Vector3[] directions, Vector3 nominal)
    {
        int n = directions.Length;
        var centroid = new Vector3(
            directions.Sum(d => d.X) / n,
            directions.Sum(d => d.Y) / n,
            directions.Sum(d => d.Z) / n);

        Symmetric3 scatter = Symmetric3.Zero;
        for (int i = 0; i < n; i++)
        {
            Vector3 d = directions[i] - centroid;
            scatter = scatter.AddOuterProduct(d.X, d.Y, d.Z);
        }

        var (values, vectors) = scatter.EigenDecomposition();

        // With no appreciable spread -- a tiny circle, or a sequence that barely
        // rotated -- the plane normal is unconstrained noise. Start from the
        // nominal axis instead and let the refinement move it.
        if (values[2] <= 0.0 || values[1] <= 1e-12 * values[2])
        {
            return nominal;
        }

        var normal = new Vector3(vectors.Get(0, 0), vectors.Get(1, 0), vectors.Get(2, 0)).Normalized();
        return normal.Dot(nominal) < 0 ? -1.0 * normal : normal;
    }

    private static double InitialRadius(Vector3[] directions, Vector3 axis)
    {
        double sum = 0.0;
        foreach (Vector3 d in directions)
        {
            sum += AngleBetween(d, axis);
        }

        return sum / directions.Length;
    }

    /// <summary>
    /// Gauss-Newton on the angular residuals. The axis is perturbed within its
    /// own tangent plane, so it stays a unit vector by construction and the
    /// parameterisation never becomes singular.
    ///
    /// Steps are accepted only if they reduce the cost, backtracking otherwise.
    /// This is not routine defensiveness: an undamped step on a poorly resolved
    /// circle -- one whose radius is not large compared with the solve noise --
    /// can fling the axis degrees away and never come back, and the runaway fit
    /// still leaves small residuals, because a huge circle passes close to a
    /// tight cluster of points. That produces exactly the failure this project
    /// treats as the worst kind: a confidently reported, badly wrong answer
    /// (D11). Rejecting uphill steps removes it.
    /// </summary>
    private static (Vector3 Axis, double Radius) Refine(Vector3[] directions, Vector3 axis, double radius)
    {
        double cost = Cost(directions, axis, radius);

        for (int iteration = 0; iteration < 64; iteration++)
        {
            var (eAlt, eAz) = TangentBasis(axis);

            Symmetric3 normalMatrix = Symmetric3.Zero;
            double g0 = 0.0, g1 = 0.0, g2 = 0.0;

            foreach (Vector3 d in directions)
            {
                Vector3 tangent = d - d.Dot(axis) * axis;
                double tangentLength = tangent.Length;

                // Directly on the axis the residual has no gradient with respect
                // to the axis direction: every way of moving the axis increases
                // the angle equally. Skip rather than divide by zero.
                if (tangentLength < 1e-15)
                {
                    continue;
                }

                tangent = tangent / tangentLength;
                double j0 = -tangent.Dot(eAlt);
                double j1 = -tangent.Dot(eAz);
                const double j2 = -1.0;

                normalMatrix = normalMatrix.AddOuterProduct(j0, j1, j2);

                double residual = AngleBetween(d, axis) - radius;
                g0 += j0 * residual;
                g1 += j1 * residual;
                g2 += j2 * residual;
            }

            if (!normalMatrix.TryInvert(out Symmetric3 inverse))
            {
                break;
            }

            double step0 = -(inverse.M00 * g0 + inverse.M01 * g1 + inverse.M02 * g2);
            double step1 = -(inverse.M01 * g0 + inverse.M11 * g1 + inverse.M12 * g2);
            double step2 = -(inverse.M02 * g0 + inverse.M12 * g1 + inverse.M22 * g2);

            double scale = 1.0;
            bool accepted = false;

            for (int backtrack = 0; backtrack < 32; backtrack++)
            {
                Vector3 candidateAxis = (axis + (scale * step0) * eAlt + (scale * step1) * eAz).Normalized();
                double candidateRadius = radius + scale * step2;

                // A circle's angular radius is the angle between the optical and
                // polar axes, so it lies strictly between 0 and 180 degrees.
                // Anything outside that is a runaway, not a solution.
                if (candidateRadius > 0.0 && candidateRadius < Math.PI)
                {
                    double candidateCost = Cost(directions, candidateAxis, candidateRadius);
                    if (candidateCost < cost)
                    {
                        axis = candidateAxis;
                        radius = candidateRadius;
                        cost = candidateCost;
                        accepted = true;
                        break;
                    }
                }

                scale *= 0.5;
            }

            if (!accepted)
            {
                break;
            }

            if (scale * (Math.Abs(step0) + Math.Abs(step1) + Math.Abs(step2)) < 1e-14)
            {
                break;
            }
        }

        return (axis, radius);
    }

    private static double Cost(Vector3[] directions, Vector3 axis, double radius)
    {
        double sum = 0.0;
        foreach (Vector3 d in directions)
        {
            double residual = AngleBetween(d, axis) - radius;
            sum += residual * residual;
        }

        return sum;
    }

    private static AxisUncertainty ComputeUncertainty(Vector3[] directions, Vector3 axis, double noiseRadians)
    {
        var (eAlt, eAz) = TangentBasis(axis);

        Symmetric3 normalMatrix = Symmetric3.Zero;
        foreach (Vector3 d in directions)
        {
            Vector3 tangent = d - d.Dot(axis) * axis;
            double tangentLength = tangent.Length;
            if (tangentLength < 1e-15)
            {
                continue;
            }

            tangent = tangent / tangentLength;
            normalMatrix = normalMatrix.AddOuterProduct(-tangent.Dot(eAlt), -tangent.Dot(eAz), -1.0);
        }

        var (values, _) = normalMatrix.EigenDecomposition();
        double conditionNumber = values[0] > 0.0
            ? values[2] / values[0]
            : double.PositiveInfinity;

        if (!normalMatrix.TryInvert(out Symmetric3 inverse))
        {
            return new AxisUncertainty(
                double.PositiveInfinity, double.PositiveInfinity, double.PositiveInfinity,
                0.0, conditionNumber);
        }

        // Marginal covariance of the axis direction: the (altitude, azimuth)
        // block of the inverted normal matrix, which already accounts for the
        // radius being fitted alongside it. That marginalisation is where a
        // narrow sweep shows up as a large azimuth-along-arc uncertainty.
        double varAlt = noiseRadians * noiseRadians * inverse.M00;
        double varAz = noiseRadians * noiseRadians * inverse.M11;
        double covAltAz = noiseRadians * noiseRadians * inverse.M01;

        double sigmaAlt = Math.Sqrt(Math.Max(varAlt, 0.0));
        double sigmaAzOnSky = Math.Sqrt(Math.Max(varAz, 0.0));
        double correlation = sigmaAlt > 0 && sigmaAzOnSky > 0
            ? Math.Clamp(covAltAz / (sigmaAlt * sigmaAzOnSky), -1.0, 1.0)
            : 0.0;

        double cosAltitude = Math.Sqrt(Math.Max(axis.X * axis.X + axis.Y * axis.Y, 0.0));
        double sigmaAzAngle = cosAltitude > 1e-9
            ? sigmaAzOnSky / cosAltitude
            : double.PositiveInfinity;

        return new AxisUncertainty(
            sigmaAlt * RadiansToArcseconds,
            sigmaAzOnSky * RadiansToArcseconds,
            sigmaAzAngle * RadiansToArcseconds,
            correlation,
            conditionNumber);
    }

    /// <summary>
    /// Orthonormal tangent basis at <paramref name="axis"/>, aligned with
    /// increasing altitude and increasing azimuth. Choosing the basis this way
    /// rather than arbitrarily is what makes the covariance readable: a step
    /// along the first vector is a step in altitude, and a step along the second
    /// is a step in azimuth scaled by cos(altitude).
    /// </summary>
    private static (Vector3 Altitude, Vector3 Azimuth) TangentBasis(Vector3 axis)
    {
        double cosAltitude = Math.Sqrt(Math.Max(axis.X * axis.X + axis.Y * axis.Y, 0.0));

        // Axis at the zenith: azimuth is degenerate, so any perpendicular pair
        // will do and the azimuth figures are reported as infinite elsewhere.
        if (cosAltitude < 1e-12)
        {
            return (new Vector3(1, 0, 0), new Vector3(0, 1, 0));
        }

        double cosAzimuth = axis.X / cosAltitude;
        double sinAzimuth = axis.Y / cosAltitude;
        double sinAltitude = axis.Z;

        var altitudeDirection = new Vector3(
            -sinAltitude * cosAzimuth,
            -sinAltitude * sinAzimuth,
            cosAltitude);
        var azimuthDirection = new Vector3(-sinAzimuth, cosAzimuth, 0.0);

        return (altitudeDirection, azimuthDirection);
    }

    private static double AngleBetween(Vector3 a, Vector3 b)
    {
        Vector3 cross = a.Cross(b);
        return Math.Atan2(cross.Length, a.Dot(b));
    }

    /// <summary>Horizon-frame unit vector: x north, y east, z up.</summary>
    internal static Vector3 ToHorizonVector(HorizontalCoordinates coordinates)
    {
        double altitude = coordinates.AltitudeDegrees * DegreesToRadians;
        double azimuth = coordinates.AzimuthDegrees * DegreesToRadians;
        double cosAltitude = Math.Cos(altitude);
        return new Vector3(
            cosAltitude * Math.Cos(azimuth),
            cosAltitude * Math.Sin(azimuth),
            Math.Sin(altitude));
    }

    internal static HorizontalCoordinates ToHorizontalCoordinates(Vector3 direction)
    {
        double azimuth = Math.Atan2(direction.Y, direction.X);
        if (azimuth < 0)
        {
            azimuth += 2.0 * Math.PI;
        }

        double altitude = Math.Atan2(direction.Z, Math.Sqrt(direction.X * direction.X + direction.Y * direction.Y));
        return new HorizontalCoordinates(azimuth / DegreesToRadians, altitude / DegreesToRadians);
    }
}
