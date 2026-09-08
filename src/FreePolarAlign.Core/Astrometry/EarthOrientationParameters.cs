namespace FreePolarAlign.Core.Astrometry;

/// <summary>
/// UT1-UTC and polar motion at one instant. These are measured quantities
/// published by IERS, not computable from first principles -- unlike
/// precession, nutation and aberration, they depend on the Earth's actual,
/// irregular rotation, so they can only be supplied, never derived.
///
/// Omitting them (<see cref="Zero"/>) costs about 14 arcseconds of pointing at
/// worst: UT1-UTC stays within roughly +/-0.9 s because of the leap second
/// rules, and the Earth turns 15.041 arcsec per second of UT1; polar motion
/// adds a few tenths of an arcsecond. That is immaterial against this
/// project's 10 arcminute alignment threshold (D5, D14) -- polar alignment
/// measures the *difference* between the mount axis and the pole from plate
/// solves, and a common rotation offset largely cancels -- so the shipping
/// product does not need an IERS feed. It matters only when reproducing
/// another implementation to sub-arcsecond agreement, as the Phase 0 exit
/// criterion does.
/// </summary>
/// <param name="Ut1MinusUtcSeconds">UT1 - UTC, seconds. Bounded to within about +/-0.9 s in practice (leap seconds keep it there).</param>
/// <param name="PolarMotionXArcsec">Polar motion x (toward the Greenwich meridian), arcseconds. Typically a few tenths of an arcsecond.</param>
/// <param name="PolarMotionYArcsec">Polar motion y (toward 90 deg W), arcseconds.</param>
public sealed record EarthOrientationParameters(double Ut1MinusUtcSeconds, double PolarMotionXArcsec, double PolarMotionYArcsec)
{
    /// <summary>
    /// UT1=UTC, no polar motion: the default, and the honest one. Interpolating
    /// a sparse snapshot of historical IERS values would be worse than this,
    /// not better -- UT1-UTC oscillates by more than a second and resets at
    /// each leap second, so interpolating between distant samples invents a
    /// value rather than approximating one. Callers needing arcsecond fidelity
    /// must pass measured values through.
    /// </summary>
    public static readonly EarthOrientationParameters Zero = new(0.0, 0.0, 0.0);
}
