namespace FreePolarAlign.Core.Astrometry;

/// <summary>A plain 3-vector, used for unit direction vectors in various reference frames.</summary>
internal readonly struct Vector3
{
    public readonly double X, Y, Z;

    public Vector3(double x, double y, double z)
    {
        X = x;
        Y = y;
        Z = z;
    }

    public static Vector3 operator +(Vector3 a, Vector3 b) => new(a.X + b.X, a.Y + b.Y, a.Z + b.Z);

    public static Vector3 operator -(Vector3 a, Vector3 b) => new(a.X - b.X, a.Y - b.Y, a.Z - b.Z);

    public static Vector3 operator *(double s, Vector3 v) => new(s * v.X, s * v.Y, s * v.Z);

    public static Vector3 operator /(Vector3 v, double s) => new(v.X / s, v.Y / s, v.Z / s);

    public double Dot(Vector3 other) => X * other.X + Y * other.Y + Z * other.Z;

    public double Length => Math.Sqrt(Dot(this));

    public Vector3 Normalized() => this / Length;

    /// <summary>Unit vector from spherical (lon, lat), both radians -- matches ERFA eraS2c.</summary>
    public static Vector3 FromSpherical(double lonRadians, double latRadians)
    {
        double cLat = Math.Cos(latRadians);
        return new Vector3(Math.Cos(lonRadians) * cLat, Math.Sin(lonRadians) * cLat, Math.Sin(latRadians));
    }

    /// <summary>Spherical (lon in [0, 2*pi), lat), both radians -- matches ERFA eraC2s.</summary>
    public (double LonRadians, double LatRadians) ToSpherical()
    {
        double d2 = X * X + Y * Y;
        double lon = d2 == 0.0 ? 0.0 : Math.Atan2(Y, X);
        if (lon < 0)
        {
            lon += 2.0 * Math.PI;
        }

        double lat = Z == 0.0 ? 0.0 : Math.Atan2(Z, Math.Sqrt(d2));
        return (lon, lat);
    }
}

/// <summary>
/// A 3x3 rotation matrix, following the same row-major, "rotate the axes"
/// convention as IAU SOFA/ERFA (rx.c/ry.c/rz.c): <see cref="RotateX"/> etc.
/// return the elementary matrix R1(angle) etc., and applying a matrix to a
/// previously-built matrix via <see cref="Multiply(in Matrix3)"/> pre-multiplies,
/// matching eraRx/eraRy/eraRz's in-place semantics (r := R(angle) * r).
/// </summary>
internal readonly struct Matrix3
{
    private readonly double _m00, _m01, _m02, _m10, _m11, _m12, _m20, _m21, _m22;

    public Matrix3(
        double m00, double m01, double m02,
        double m10, double m11, double m12,
        double m20, double m21, double m22)
    {
        _m00 = m00; _m01 = m01; _m02 = m02;
        _m10 = m10; _m11 = m11; _m12 = m12;
        _m20 = m20; _m21 = m21; _m22 = m22;
    }

    public static readonly Matrix3 Identity = new(1, 0, 0, 0, 1, 0, 0, 0, 1);

    public static Matrix3 RotateX(double angleRadians)
    {
        double s = Math.Sin(angleRadians), c = Math.Cos(angleRadians);
        return new Matrix3(
            1, 0, 0,
            0, c, s,
            0, -s, c);
    }

    public static Matrix3 RotateY(double angleRadians)
    {
        double s = Math.Sin(angleRadians), c = Math.Cos(angleRadians);
        return new Matrix3(
            c, 0, -s,
            0, 1, 0,
            s, 0, c);
    }

    public static Matrix3 RotateZ(double angleRadians)
    {
        double s = Math.Sin(angleRadians), c = Math.Cos(angleRadians);
        return new Matrix3(
            c, s, 0,
            -s, c, 0,
            0, 0, 1);
    }

    /// <summary>Returns <c>this * other</c> (standard matrix product).</summary>
    public Matrix3 Multiply(in Matrix3 other)
    {
        return new Matrix3(
            _m00 * other._m00 + _m01 * other._m10 + _m02 * other._m20,
            _m00 * other._m01 + _m01 * other._m11 + _m02 * other._m21,
            _m00 * other._m02 + _m01 * other._m12 + _m02 * other._m22,

            _m10 * other._m00 + _m11 * other._m10 + _m12 * other._m20,
            _m10 * other._m01 + _m11 * other._m11 + _m12 * other._m21,
            _m10 * other._m02 + _m11 * other._m12 + _m12 * other._m22,

            _m20 * other._m00 + _m21 * other._m10 + _m22 * other._m20,
            _m20 * other._m01 + _m21 * other._m11 + _m22 * other._m21,
            _m20 * other._m02 + _m21 * other._m12 + _m22 * other._m22);
    }

    public Vector3 Apply(in Vector3 v) => new(
        _m00 * v.X + _m01 * v.Y + _m02 * v.Z,
        _m10 * v.X + _m11 * v.Y + _m12 * v.Z,
        _m20 * v.X + _m21 * v.Y + _m22 * v.Z);

    public double Get(int row, int col) => (row, col) switch
    {
        (0, 0) => _m00,
        (0, 1) => _m01,
        (0, 2) => _m02,
        (1, 0) => _m10,
        (1, 1) => _m11,
        (1, 2) => _m12,
        (2, 0) => _m20,
        (2, 1) => _m21,
        (2, 2) => _m22,
        _ => throw new ArgumentOutOfRangeException(nameof(row))
    };
}
