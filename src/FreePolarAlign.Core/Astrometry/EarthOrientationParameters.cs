namespace FreePolarAlign.Core.Astrometry;

/// <summary>
/// UT1-UTC and polar motion at one instant. These are measured quantities
/// published by IERS, not computable from first principles -- unlike
/// precession, nutation and aberration, they depend on the Earth's actual,
/// irregular rotation, so they can only be supplied, never derived.
///
/// Omitting them (<see cref="Zero"/>) costs about 14 arcseconds of pointing at
/// worst: UT1-UTC stays within roughly +/-0.9 s because of the leap second
/// rules, and the Earth turns 15.041 arcsec per second of UT1.
///
/// For polar alignment specifically that is far cheaper than it looks, and the
/// reason matters. A UT1 error is a rotation *about* the polar axis, so it does
/// not move the pole in the local horizon frame at all; it perturbs the measured
/// misalignment only at second order, as the product of the misalignment and the
/// rotation. Even a 2 degree misalignment with 2 seconds of clock error yields
/// about 1 arcsecond of error in the result. Polar motion does move the pole
/// relative to the ground, but only by a few tenths of an arcsecond. Both sit
/// inside the roughly 6 arcsecond accuracy this project needs on the reported
/// figure, so the shipping product does not need an IERS feed.
///
/// Supplying real values matters when reproducing another implementation to
/// sub-arcsecond *pointing* agreement, as the Phase 0 exit criterion does.
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
