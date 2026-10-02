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
    /// Value and its uncertainty, sign preserved, e.g. "+1° 24.9' ± 0.4'".
    /// Used for the altitude and azimuth figures, which are the mount's own
    /// angles, where the sign says which way the error points. The uncertainty
    /// stays in arcminutes: it is always a fraction of one.
    /// </summary>
    public static string FormatSignedWithSigma(double valueArcminutes, double sigmaArcminutes) =>
        FormattableString.Invariant($"{SignedAngle(valueArcminutes)} ± {sigmaArcminutes:F1}'");

    /// <summary>
    /// A mount angle as a user sets it, in degrees and arcminutes, e.g.
    /// "1° 24.9'". Always with the degrees, even when there are none, so a
    /// figure never changes shape as the user turns past a whole degree, and
    /// "84.9'" is never left to be mistaken for degrees.
    /// </summary>
    public static string Angle(double arcminutes)
    {
        double tenths = Math.Round(Math.Abs(arcminutes) * 10.0);
        long degrees = (long)(tenths / 600.0);
        double minutes = (tenths - degrees * 600.0) / 10.0;
        return FormattableString.Invariant($"{degrees}° {minutes:F1}'");
    }

    private static string SignedAngle(double arcminutes) =>
        (Math.Round(arcminutes * 10.0) < 0.0 ? "-" : "+") + Angle(arcminutes);

    /// <summary>
    /// Below this a correction is not worth turning a bolt for: a tenth of the
    /// display's own resolution would be shown as 0.0' and read as "done".
    /// </summary>
    private const double NegligibleCorrectionArcminutes = 0.05;

    /// <summary>
    /// Which way to turn the altitude adjustment, and how far. The error is the
    /// axis minus the pole, so a positive one means the axis is too high.
    /// </summary>
    public static string AltitudeCorrection(double altitudeErrorArcminutes)
    {
        double size = Math.Abs(altitudeErrorArcminutes);
        if (size < NegligibleCorrectionArcminutes)
        {
            return "Altitude: leave it where it is";
        }

        string way = altitudeErrorArcminutes > 0 ? "Lower" : "Raise";
        return $"{way} the altitude by {Angle(size)}";
    }

    /// <summary>
    /// Which way to turn the azimuth adjustment, and how far, as seen standing
    /// behind the mount and looking along the polar axis towards the pole.
    ///
    /// A positive error is the axis clockwise of the pole seen from above. For
    /// someone facing the pole, clockwise is to their right in both hemispheres
    /// -- facing north, east is on the right; facing south, west is -- so a
    /// positive error is always corrected to the left. The compass direction
    /// that "left" means is what differs, and it is given too, because the
    /// azimuth knobs on most mounts are labelled with neither.
    /// </summary>
    public static string AzimuthCorrection(double azimuthErrorArcminutes, double siteLatitudeDegrees)
    {
        double size = Math.Abs(azimuthErrorArcminutes);
        if (size < NegligibleCorrectionArcminutes)
        {
            return "Azimuth: leave it where it is";
        }

        bool left = azimuthErrorArcminutes > 0;
        bool north = siteLatitudeDegrees >= 0;
        string compass = left == north ? "west" : "east";
        return $"Move the azimuth {(left ? "left" : "right")} ({compass}) by {Angle(size)}";
    }

    /// <summary>How far the bolts have moved the axis since the sweep, signed as the errors are (D29).</summary>
    public static string AppliedBolts(double altitudeArcminutes, double azimuthArcminutes) =>
        $"Turned since the sweep: altitude {SignedAngle(altitudeArcminutes)}, azimuth {SignedAngle(azimuthArcminutes)}";

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

    /// <summary>
    /// One line saying what the engine is doing about the next sample (D26), or
    /// null outside a sequence. The user turning a mount by hand watches this to
    /// know whether to keep turning, hold still, or wait.
    ///
    /// Solve failures are counted here rather than raised as a warning. While
    /// the mount is being moved most frames fail, so the count measures the
    /// user's pace, not a fault; a banner for each would be up all the time.
    /// </summary>
    /// <param name="now">Passed in so the countdown can be tested without a clock.</param>
    public static string? SamplingStatus(UiState state, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(state);

        if (!state.SessionActive)
        {
            return null;
        }

        SamplingView sampling = state.Sampling;
        string failures = sampling.ConsecutiveFailures > 0 && sampling.Activity != SamplingActivity.Failed
            ? FormattableString.Invariant($" Last solve failed ({sampling.ConsecutiveFailures} in a row).")
            : string.Empty;

        // Checked first: any motion cancels a pending solve, so a countdown
        // shown while the mount is moving would be counting down to nothing.
        if (state.MountIsMoving)
        {
            return "Mount moving: nothing is sampled until it stops." + failures;
        }

        string? line = sampling.Activity switch
        {
            SamplingActivity.Scheduled => sampling.Trigger switch
            {
                SampleTrigger.SlewEnded => $"Settling: solving {Countdown(sampling.SolveDueUtc, now)}.",
                SampleTrigger.Retry => $"Retrying the solve {Countdown(sampling.SolveDueUtc, now)}.",
                SampleTrigger.Forced => "Sample requested: solving the next frame to start.",
                _ => $"Next solve {Countdown(sampling.SolveDueUtc, now)}.",
            },
            SamplingActivity.Solving => "Solving...",
            SamplingActivity.Skipped => sampling.Detail,
            SamplingActivity.Failed => FormattableString.Invariant(
                $"Last solve failed ({sampling.ConsecutiveFailures} in a row): {sampling.Detail}"),
            SamplingActivity.Sampled => "Sample taken.",
            SamplingActivity.Live => "Live: every frame is solved, and the figures are from the latest.",
            _ => null,
        };

        return line is null ? null : line + failures;
    }

    private static string Countdown(DateTimeOffset? due, DateTimeOffset now)
    {
        if (due is not { } at)
        {
            return "shortly";
        }

        double seconds = Math.Ceiling((at - now).TotalSeconds);
        return seconds <= 0.0 ? "now" : FormattableString.Invariant($"in {seconds:F0} s");
    }
}
