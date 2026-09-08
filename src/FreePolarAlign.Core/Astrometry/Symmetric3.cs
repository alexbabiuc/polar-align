namespace FreePolarAlign.Core.Astrometry;

/// <summary>
/// A symmetric 3x3 matrix with the eigen-decomposition and inverse the
/// small-circle fit needs. Separate from <see cref="Matrix3"/> because
/// symmetry is what makes the Jacobi rotation below valid, and because the
/// eigenvalues are wanted in their own right: they *are* the conditioning of
/// the fit, not just a step toward inverting it.
/// </summary>
internal readonly struct Symmetric3
{
    public readonly double M00, M01, M02, M11, M12, M22;

    public Symmetric3(double m00, double m01, double m02, double m11, double m12, double m22)
    {
        M00 = m00; M01 = m01; M02 = m02; M11 = m11; M12 = m12; M22 = m22;
    }

    public static Symmetric3 Zero => new(0, 0, 0, 0, 0, 0);

    /// <summary>Accumulates the outer product <c>v v^T</c> into this matrix.</summary>
    public Symmetric3 AddOuterProduct(double v0, double v1, double v2) => new(
        M00 + v0 * v0, M01 + v0 * v1, M02 + v0 * v2,
        M11 + v1 * v1, M12 + v1 * v2, M22 + v2 * v2);

    public double Get(int row, int col) => (row, col) switch
    {
        (0, 0) => M00,
        (0, 1) or (1, 0) => M01,
        (0, 2) or (2, 0) => M02,
        (1, 1) => M11,
        (1, 2) or (2, 1) => M12,
        (2, 2) => M22,
        _ => throw new ArgumentOutOfRangeException(nameof(row))
    };

    /// <summary>
    /// Eigenvalues (ascending) and the corresponding orthonormal eigenvectors,
    /// as columns of the returned matrix, by cyclic Jacobi rotation. Iterative
    /// rather than closed-form: the closed-form solution for a symmetric 3x3
    /// loses accuracy badly for nearly-degenerate eigenvalues, which is exactly
    /// the regime a narrow RA sweep produces (see
    /// <c>SmallCircleFit</c>'s conditioning discussion).
    /// </summary>
    public (double[] Eigenvalues, Matrix3 Eigenvectors) EigenDecomposition()
    {
        double[,] a =
        {
            { M00, M01, M02 },
            { M01, M11, M12 },
            { M02, M12, M22 },
        };
        double[,] v = { { 1, 0, 0 }, { 0, 1, 0 }, { 0, 0, 1 } };

        for (int sweep = 0; sweep < 64; sweep++)
        {
            double off = Math.Abs(a[0, 1]) + Math.Abs(a[0, 2]) + Math.Abs(a[1, 2]);
            if (off < 1e-300)
            {
                break;
            }

            for (int p = 0; p < 2; p++)
            {
                for (int q = p + 1; q < 3; q++)
                {
                    if (Math.Abs(a[p, q]) <= 1e-18 * (Math.Abs(a[p, p]) + Math.Abs(a[q, q])))
                    {
                        continue;
                    }

                    double theta = (a[q, q] - a[p, p]) / (2.0 * a[p, q]);
                    double t = Math.Sign(theta) / (Math.Abs(theta) + Math.Sqrt(theta * theta + 1.0));
                    if (theta == 0.0)
                    {
                        t = 1.0;
                    }

                    double c = 1.0 / Math.Sqrt(t * t + 1.0);
                    double s = t * c;

                    for (int k = 0; k < 3; k++)
                    {
                        double akp = a[k, p], akq = a[k, q];
                        a[k, p] = c * akp - s * akq;
                        a[k, q] = s * akp + c * akq;
                    }

                    for (int k = 0; k < 3; k++)
                    {
                        double apk = a[p, k], aqk = a[q, k];
                        a[p, k] = c * apk - s * aqk;
                        a[q, k] = s * apk + c * aqk;
                    }

                    for (int k = 0; k < 3; k++)
                    {
                        double vkp = v[k, p], vkq = v[k, q];
                        v[k, p] = c * vkp - s * vkq;
                        v[k, q] = s * vkp + c * vkq;
                    }
                }
            }
        }

        var pairs = new (double Value, int Index)[] { (a[0, 0], 0), (a[1, 1], 1), (a[2, 2], 2) }
            .OrderBy(x => x.Value)
            .ToArray();

        var eigenvalues = new[] { pairs[0].Value, pairs[1].Value, pairs[2].Value };
        int i0 = pairs[0].Index, i1 = pairs[1].Index, i2 = pairs[2].Index;
        var eigenvectors = new Matrix3(
            v[0, i0], v[0, i1], v[0, i2],
            v[1, i0], v[1, i1], v[1, i2],
            v[2, i0], v[2, i1], v[2, i2]);

        return (eigenvalues, eigenvectors);
    }

    /// <summary>
    /// Inverse via the eigen-decomposition, which lets a caller see *why* an
    /// inversion is unreliable rather than only that it is. Returns false when
    /// the matrix is singular to within <paramref name="conditionLimit"/>,
    /// because a covariance built from a numerically singular normal matrix is
    /// a confident wrong answer, and this project would rather report nothing
    /// (D11).
    /// </summary>
    public bool TryInvert(out Symmetric3 inverse, double conditionLimit = 1e12)
    {
        var (values, vectors) = EigenDecomposition();
        double largest = Math.Max(Math.Abs(values[2]), double.Epsilon);

        if (values[0] <= 0.0 || largest / values[0] > conditionLimit)
        {
            inverse = Zero;
            return false;
        }

        Symmetric3 result = Zero;
        for (int k = 0; k < 3; k++)
        {
            double scale = 1.0 / values[k];
            double e0 = vectors.Get(0, k), e1 = vectors.Get(1, k), e2 = vectors.Get(2, k);
            result = new Symmetric3(
                result.M00 + scale * e0 * e0,
                result.M01 + scale * e0 * e1,
                result.M02 + scale * e0 * e2,
                result.M11 + scale * e1 * e1,
                result.M12 + scale * e1 * e2,
                result.M22 + scale * e2 * e2);
        }

        inverse = result;
        return true;
    }
}
