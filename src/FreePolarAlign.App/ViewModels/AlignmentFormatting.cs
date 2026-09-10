using FreePolarAlign.Core.Engine;

namespace FreePolarAlign.App.ViewModels;

/// <summary>
/// Number and success-threshold presentation, factored out of any view so it
/// can be unit-tested without Avalonia. This is the single place the 10
/// arcminute success indication is decided, and the single place arcminute
/// values are turned into text -- both are exactly the kind of "getting it
/// wrong is the single easiest way to make the UI lie" logic the brief calls
/// out, so it does not belong scattered across XAML converters.
/// </summary>
public static class AlignmentFormatting
{
    /// <summary>
    /// D2/roadmap: a success *indication*, not an error budget. The measurement
    /// itself is required to be far better than this (see "What 10 arcminutes
    /// means" in DECISIONS.md); this constant is purely the point at which the
    /// UI tells the user their alignment is good enough to stop turning bolts.
    /// </summary>
    public const double SuccessThresholdArcminutes = 10.0;

    /// <summary>
    /// Whether the total error is under the success threshold. Deliberately
    /// keyed on <see cref="AlignmentEstimate.TotalErrorArcminutes"/> alone --
    /// not on the altitude or azimuth figures individually, and not on their
    /// sum, which is neither this quantity nor the one the roadmap's success
    /// indication is judged on (see AlignmentEstimate's own doc comment).
    /// </summary>
    public static bool IsGoodEnoughToStop(AlignmentEstimate estimate)
    {
        ArgumentNullException.ThrowIfNull(estimate);
        return estimate.TotalErrorArcminutes < SuccessThresholdArcminutes;
    }

    /// <summary>Value and its uncertainty, e.g. "12.3' ± 0.4'". Unsigned -- for the total error and residual-style figures.</summary>
    public static string FormatMagnitudeWithSigma(double valueArcminutes, double sigmaArcminutes) =>
        FormattableString.Invariant($"{Math.Abs(valueArcminutes):F1}' ± {sigmaArcminutes:F1}'");

    /// <summary>
    /// Value and its uncertainty, sign preserved, e.g. "+3.2' ± 0.4'" or
    /// "-1.1' ± 0.4'". Used for the altitude and azimuth bolt figures, where
    /// the sign is meaningful (which way the error currently points) even
    /// though the on-screen correction *arrow* is explicitly out of scope here
    /// (D12; that geometry is being built elsewhere).
    /// </summary>
    public static string FormatSignedWithSigma(double valueArcminutes, double sigmaArcminutes)
    {
        string sign = valueArcminutes < 0 ? "-" : "+";
        return FormattableString.Invariant($"{sign}{Math.Abs(valueArcminutes):F1}' ± {sigmaArcminutes:F1}'");
    }

    public static string FormatResidualRms(double residualRmsArcseconds) =>
        FormattableString.Invariant($"{residualRmsArcseconds:F1}\"");

    /// <summary>
    /// Which hemisphere a site latitude falls in. Display-only: this project's
    /// on-screen correction direction and parity are derived from the solved
    /// CD matrix (D12), not guessed from hemisphere, and that reticle/parity
    /// work is explicitly out of scope here. This exists only so the UI can
    /// say "Northern hemisphere" next to the numbers rather than nothing.
    /// </summary>
    public static string Hemisphere(double siteLatitudeDegrees) =>
        siteLatitudeDegrees >= 0.0 ? "Northern" : "Southern";

    /// <summary>
    /// Tracking state as words. "Unknown" is shown as unknown rather than as
    /// "stopped": a user who believes the drive is off when it is running will
    /// misread every reading that follows, and the two are indistinguishable in
    /// a driver that simply does not report.
    /// </summary>
    public static string Tracking(MountTrackingState state) => state switch
    {
        MountTrackingState.Tracking => "Tracking at sidereal rate",
        MountTrackingState.Stopped => "Not tracking",
        _ => "Tracking state not reported by this driver",
    };

    public static string PierSide(MeridianSide side) => side switch
    {
        MeridianSide.East => "East of the pier",
        MeridianSide.West => "West of the pier",
        _ => "Pier side not reported",
    };

    /// <summary>
    /// The mount's reported position, in the units people read off a chart.
    /// Labelled by the caller as *reported*: on a misaligned mount this differs
    /// from where the telescope actually points by exactly the error being
    /// measured, and presenting it as truth would undercut the whole point.
    /// </summary>
    public static string Coordinates(double? raDegrees, double? decDegrees) =>
        raDegrees is { } ra && decDegrees is { } dec
            ? $"RA {CoordinateText.FormatRightAscension(ra)}   Dec {CoordinateText.FormatDeclination(dec)}"
            : "--";

    /// <summary>
    /// Focal length with its provenance. "1000 mm (as entered)" and "1000 mm
    /// (measured)" mean quite different things when a solve keeps failing, so
    /// the distinction is never dropped.
    /// </summary>
    public static string FocalLength(double? millimetres, bool isSolved) =>
        millimetres is { } value
            ? FormattableString.Invariant($"{value:F1} mm ({(isSolved ? "measured from a solve" : "as entered")})")
            : "not set -- solves will be blind";

    public static string PlateScale(double? arcsecondsPerPixel, double? fieldRadiusDegrees)
    {
        if (arcsecondsPerPixel is not { } scale)
        {
            return "--";
        }

        return fieldRadiusDegrees is { } radius
            ? FormattableString.Invariant($"{scale:F2}\"/pixel, field radius {radius:F2}°")
            : FormattableString.Invariant($"{scale:F2}\"/pixel");
    }

    public static string PixelSize(double? micronsPerPixel, int widthPixels, int heightPixels) =>
        micronsPerPixel is { } microns
            ? FormattableString.Invariant($"{microns:F2} µm pixels, {widthPixels}×{heightPixels}")
            : "--";

    /// <summary>
    /// The site, once confirmed. Shown to five decimal places because a
    /// latitude error appears one-for-one in the reported altitude misalignment
    /// (D19): a hundredth of a degree is already six tenths of an arcminute of
    /// pure bias, so rounding the display to two places would hide a difference
    /// that matters.
    /// </summary>
    public static string Site(double? latitude, double? longitude, double? heightMeters)
    {
        if (latitude is not { } lat || longitude is not { } lon)
        {
            return "not confirmed";
        }

        string northSouth = lat >= 0 ? "N" : "S";
        string eastWest = lon >= 0 ? "E" : "W";
        string height = heightMeters is { } metres
            ? FormattableString.Invariant($", {metres:F0} m")
            : string.Empty;

        return FormattableString.Invariant(
            $"{Math.Abs(lat):F5}° {northSouth}, {Math.Abs(lon):F5}° {eastWest}{height}");
    }
}
