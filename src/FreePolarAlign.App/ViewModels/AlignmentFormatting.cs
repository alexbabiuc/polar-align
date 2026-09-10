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
}
